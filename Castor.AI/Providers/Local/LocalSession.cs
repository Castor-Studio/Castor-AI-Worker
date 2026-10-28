using System.Diagnostics;
using System.Threading.Channels;
using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;
using Castor.AI.Video;
using Grpc.Core;
using Grpc.Net.Client;

namespace Castor.AI.Providers.Local;

/// <summary>
/// One worker process + one gRPC session over a named pipe.
/// <see cref="Completion"/> resolves with a failure reason, or null on a requested stop.
/// </summary>
internal sealed class LocalSession : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private const int AudioSampleRate = 16_000;

    private readonly LocalWorkerOptions _options;
    private readonly Process _process;
    private readonly GrpcChannel _channel;
    private readonly AiWorker.AiWorkerClient _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ClientMessage> _outbox =
        Channel.CreateUnbounded<ClientMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Lock _audioGate = new();
    private readonly Dictionary<string, VideoRingWriter> _video = [];
    private AsyncDuplexStreamingCall<ClientMessage, WorkerMessage>? _call;
    private AudioRingWriter? _audio;
    private volatile bool _stopRequested;
    private int _disposed;

    private LocalSession(LocalWorkerOptions options, Process process, string pipeName)
    {
        _options = options;
        _process = process;
        _channel = LocalGrpcChannel.ForNamedPipe(pipeName);
        _client = new AiWorker.AiWorkerClient(_channel);
        _process.Exited += (_, _) =>
        {
            if (!_stopRequested)
            {
                _completion.TrySetResult($"Worker process exited (code {_process.ExitCode})");
            }
        };
    }

    public WorkerCapabilities Capabilities { get; private set; } = new();

    public Task<string?> Completion => _completion.Task;

    public static async Task<LocalSession> OpenAsync(
        AiProviderRequest config,
        LocalWorkerOptions options,
        Action<WorkerMessage> onMessage,
        Action<string> onLog,
        Action<string> onProgress,
        CancellationToken cancellationToken)
    {
        var pipeName = $"castor-ai-{Guid.NewGuid():N}";
        if (options.Device is not ("cpu" or "gpu"))
            throw new ArgumentException("Device must be 'cpu' or 'gpu'.", nameof(options));
        if (options.GpuAdapter < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "GPU adapter must be non-negative.");
        var process = StartWorker(WorkerLocator.Find(options.WorkerPath), config.Domain, pipeName, options.Device, options.GpuAdapter, onLog);
        var session = new LocalSession(options, process, pipeName);
        try
        {
            await session.HandshakeAsync(config, onMessage, onLog, onProgress, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public bool Send(ClientMessage message) => !_stopRequested && _outbox.Writer.TryWrite(message);

    public bool PushAudio(ReadOnlySpan<float> interleaved, long ptsUs)
    {
        lock (_audioGate)
        {
            if (_audio is null || _stopRequested)
            {
                return false;
            }

            _audio.Write(interleaved);
            return Send(new ClientMessage { Audio = new AudioReady { WriteFrame = (ulong)_audio.WriteFrame, PtsUs = ptsUs } });
        }
    }

    public bool PushVideo(string cameraId, ReadOnlySpan<byte> pixels, long ptsUs)
    {
        if (!_video.TryGetValue(cameraId, out var writer))
        {
            return false;
        }

        // One lock per camera: cameras are fed by independent threads.
        lock (writer)
        {
            if (_stopRequested || Volatile.Read(ref _disposed) == 1)
            {
                return false;
            }

            var seq = writer.Write(pixels, ptsUs);
            return Send(new ClientMessage { VideoFrame = new VideoFrameReady { CameraId = cameraId, Seq = seq, PtsUs = ptsUs } });
        }
    }

    public async Task StopAsync(string reason)
    {
        if (_call is not null && !_stopRequested)
        {
            _outbox.Writer.TryWrite(new ClientMessage { Stop = new SessionStop { Reason = reason } });
            _stopRequested = true;
            _outbox.Writer.TryComplete();
            await Task.WhenAny(_completion.Task, Task.Delay(StopTimeout));
        }

        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stopRequested = true;
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        _call?.Dispose();
        lock (_audioGate)
        {
            _audio?.Dispose();
            _audio = null;
        }

        foreach (var writer in _video.Values)
        {
            lock (writer)
            {
                writer.Dispose();
            }
        }

        _channel.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(StopTimeout);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Already gone or not killable: the worker watchdog will exit with the app anyway.
        }

        _process.Dispose();
        _cts.Dispose();
        _completion.TrySetResult(null);
    }

    private static Process StartWorker(string workerPath, string domain, string pipeName, string device, int gpuAdapter, Action<string> onLog)
    {
        var startInfo = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--domain");
        startInfo.ArgumentList.Add(domain);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--gpu-adapter");
        startInfo.ArgumentList.Add(gpuAdapter.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onLog($"[worker stdout] {e.Data}");
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onLog($"[worker stderr] {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        onLog($"Worker started (pid {process.Id}, pipe {pipeName}, path {workerPath})");
        return process;
    }

    private async Task HandshakeAsync(
        AiProviderRequest config,
        Action<WorkerMessage> onMessage,
        Action<string> onLog,
        Action<string> onProgress,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var ct = linked.Token;

        await WaitReadyAsync(onLog, onProgress, ct);

        Capabilities = await _client.GetCapabilitiesAsync(new GetCapabilitiesRequest(), cancellationToken: ct);
        if (config.Cameras.Count > Capabilities.MaxCameras)
        {
            throw new InvalidOperationException(
                $"{config.Cameras.Count} cameras requested, worker supports {Capabilities.MaxCameras}");
        }

        var start = new SessionStart { SessionId = Guid.NewGuid().ToString("N")[..8], Domain = config.Domain };
        start.Cameras.AddRange(config.Cameras);
        foreach (var (key, value) in config.Parameters)
        {
            start.Params[key] = value;
        }

        var sharedMemory = new SharedMemoryTransport();
        if (config.AudioChannels > 0)
        {
            var preferredRate = Capabilities.PreferredAudio?.SampleRate ?? 0;
            if (preferredRate != AudioSampleRate)
            {
                throw new InvalidOperationException($"Worker wants audio at {preferredRate} Hz, the app produces {AudioSampleRate} Hz");
            }

            var name = AudioRingLayout.RegionName(start.SessionId);
            _audio = AudioRingWriter.Create(
                name, config.AudioChannels, AudioSampleRate, AudioSampleRate * _options.AudioRingSeconds, config.ClockUs());
            sharedMemory.Audio = new AudioRegion { Name = name, SizeBytes = (ulong)_audio.SizeBytes };
            onLog($"Audio ring '{name}': {config.AudioChannels} channels @ {AudioSampleRate} Hz, {_options.AudioRingSeconds} s");
        }

        if (VideoFeed.WantedFormat(Capabilities) is { } video)
        {
            for (var i = 0; i < config.Cameras.Count; i++)
            {
                var cameraId = config.Cameras[i].CameraId;
                var name = VideoRingLayout.RegionName(start.SessionId, i);
                var writer = VideoRingWriter.Create(name, (int)video.Width, (int)video.Height, video.PixelFormat);
                _video[cameraId] = writer;
                sharedMemory.Video.Add(new VideoRegion { CameraId = cameraId, Name = name, SizeBytes = (ulong)writer.SizeBytes });
            }

            onLog($"Video regions: {config.Cameras.Count} x {video.Width}x{video.Height} {video.PixelFormat}, " +
                  $"{VideoRingLayout.DefaultSlotCount} slots");
        }

        if (sharedMemory.Audio is not null || sharedMemory.Video.Count > 0)
        {
            start.Transport = new MediaTransport { SharedMemory = sharedMemory };
        }

        _call = _client.Session(cancellationToken: _cts.Token);
        await _call.RequestStream.WriteAsync(new ClientMessage { Start = start }, ct);

        if (!await _call.ResponseStream.MoveNext(ct))
        {
            throw new InvalidOperationException("Worker closed the session before accepting it");
        }

        var first = _call.ResponseStream.Current;
        onMessage(first);
        switch (first.BodyCase)
        {
            case WorkerMessage.BodyOneofCase.Accepted:
                break;
            case WorkerMessage.BodyOneofCase.Error:
                throw new InvalidOperationException($"Session refused: {first.Error.Code} {first.Error.Message}");
            default:
                throw new InvalidOperationException($"Unexpected first message: {first.BodyCase}");
        }

        _ = WriteLoopAsync();
        _ = ReadLoopAsync(onMessage);
    }

    private async Task WaitReadyAsync(Action<string> onLog, Action<string> onProgress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _options.ReadyTimeout;
        HealthResponse.Types.Status? lastStatus = null;
        var lastDetail = "";
        while (true)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"Worker exited during startup (code {_process.ExitCode})");
            }

            try
            {
                var health = await _client.HealthAsync(
                    new HealthRequest(), deadline: DateTime.UtcNow.AddSeconds(1), cancellationToken: ct);
                if (health.Status != lastStatus)
                {
                    onLog($"Health: {health.Status}");
                    lastStatus = health.Status;
                }

                // Model download or loading in progress: the timeout counts from the last progress.
                if (health.Detail.Length > 0 && health.Detail != lastDetail)
                {
                    lastDetail = health.Detail;
                    deadline = DateTime.UtcNow + _options.ReadyTimeout;
                    onProgress(health.Detail);
                }

                if (health.Status == HealthResponse.Types.Status.Ready)
                {
                    return;
                }
            }
            catch (RpcException) when (!ct.IsCancellationRequested)
            {
                // Pipe not created yet.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(lastDetail.Length > 0
                    ? $"Worker stuck for {_options.ReadyTimeout.TotalSeconds} s at: {lastDetail}"
                    : $"Worker not ready after {_options.ReadyTimeout.TotalSeconds} s");
            }

            await Task.Delay(100, ct);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _call!.RequestStream.WriteAsync(message);
            }

            await _call!.RequestStream.CompleteAsync();
        }
        catch (Exception) when (_stopRequested)
        {
        }
        catch (Exception ex)
        {
            _completion.TrySetResult($"Send failed: {ex.Message}");
        }
    }

    private async Task ReadLoopAsync(Action<WorkerMessage> onMessage)
    {
        string? failure;
        try
        {
            await foreach (var message in _call!.ResponseStream.ReadAllAsync(_cts.Token))
            {
                onMessage(message);
            }

            failure = _stopRequested ? null : "Worker closed the session";
        }
        catch (Exception) when (_stopRequested)
        {
            failure = null;
        }
        catch (RpcException ex)
        {
            failure = $"gRPC stream lost: {ex.StatusCode} {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            failure = $"gRPC stream lost: {ex.Message}";
        }

        _completion.TrySetResult(failure);
    }
}
