using Castor.AI.Contracts.V1;

namespace Castor.AI.Providers.Local;

/// <summary>
/// Runs the worker as a child process on this machine (gRPC over a named pipe, media in shared memory).
/// Supervises it and reopens the session with backoff when the process or the stream dies.
/// </summary>
public sealed class LocalWorkerProvider(LocalWorkerOptions? options = null) : IAiProvider
{
    private readonly LocalWorkerOptions _options = options ?? new LocalWorkerOptions();

    private AiProviderRequest? _config;
    private LocalSession? _session;
    private CancellationTokenSource? _lifetime;
    private volatile bool _stopping;
    private int _restartAttempt;

    public string Name => "local";

    public event Action<AiConnectionState, string>? StateChanged;

    public event Action<WorkerCapabilities>? SessionStarted;

    public event Action<WorkerMessage>? MessageReceived;

    public event Action<string>? Log;

    public async Task StartAsync(AiProviderRequest config, CancellationToken cancellationToken = default)
    {
        if (_config is not null)
        {
            throw new InvalidOperationException("Session already started");
        }

        _config = config;
        _stopping = false;
        _restartAttempt = 0;
        _lifetime = new CancellationTokenSource();

        SetState(AiConnectionState.Starting, "Starting worker");
        try
        {
            await OpenSessionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _config = null;
            _lifetime.Dispose();
            _lifetime = null;
            SetState(AiConnectionState.Faulted, ex.Message);
            throw;
        }
    }

    public bool Send(ClientMessage message) => _session?.Send(message) ?? false;

    public bool PushAudio(ReadOnlySpan<float> interleaved, long ptsUs) =>
        _session?.PushAudio(interleaved, ptsUs) ?? false;

    public bool PushVideo(string cameraId, ReadOnlySpan<byte> pixels, long ptsUs) =>
        _session?.PushVideo(cameraId, pixels, ptsUs) ?? false;

    public async Task StopAsync()
    {
        _stopping = true;
        _lifetime?.Cancel();

        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.StopAsync("stopped by user");
        }

        _lifetime?.Dispose();
        _lifetime = null;
        _config = null;
        SetState(AiConnectionState.Stopped, "Session stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_config is not null)
        {
            await StopAsync();
        }
    }

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        var session = await LocalSession.OpenAsync(
            _config!,
            _options,
            message => MessageReceived?.Invoke(message),
            line => Log?.Invoke(line),
            detail => SetState(_restartAttempt > 0 ? AiConnectionState.Restarting : AiConnectionState.Starting, detail),
            cancellationToken);

        if (_stopping)
        {
            await session.DisposeAsync();
            throw new OperationCanceledException();
        }

        _session = session;
        _restartAttempt = 0;
        SessionStarted?.Invoke(session.Capabilities);
        SetState(AiConnectionState.Running, $"Session running on {session.Capabilities.WorkerId}");
        _ = SuperviseAsync(session);
    }

    private async Task SuperviseAsync(LocalSession session)
    {
        var failure = await session.Completion;
        if (_stopping || failure is null || !ReferenceEquals(_session, session))
        {
            return;
        }

        _session = null;
        await session.DisposeAsync();
        SetState(AiConnectionState.Faulted, failure);

        while (!_stopping && _lifetime is { } lifetime)
        {
            var backoff = _options.RestartBackoff;
            var delay = backoff.Count == 0 ? TimeSpan.FromSeconds(1) : backoff[Math.Min(_restartAttempt, backoff.Count - 1)];
            _restartAttempt++;
            SetState(AiConnectionState.Restarting, $"Restarting in {delay.TotalSeconds:0} s (attempt {_restartAttempt})");
            try
            {
                await Task.Delay(delay, lifetime.Token);
                await OpenSessionAsync(lifetime.Token);
                return;
            }
            catch (Exception) when (_stopping)
            {
                return;
            }
            catch (Exception ex)
            {
                SetState(AiConnectionState.Faulted, ex.Message);
            }
        }
    }

    private void SetState(AiConnectionState state, string detail)
    {
        Log?.Invoke($"{state}: {detail}");
        StateChanged?.Invoke(state, detail);
    }
}
