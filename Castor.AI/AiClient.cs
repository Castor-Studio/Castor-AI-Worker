using Castor.AI.Arbitration;
using Castor.AI.Audio;
using Castor.AI.Contracts.V1;
using Castor.AI.Providers;
using Castor.AI.Video;
using Google.Protobuf.WellKnownTypes;

namespace Castor.AI;

/// <summary>
/// Entry point of the SDK: one AI session at a time, whatever the worker domain and wherever it runs.
/// <list type="number">
/// <item><see cref="StartAsync"/> with the cameras (scenes) the AI may choose;</item>
/// <item>feed it: <see cref="WriteAudio"/> per microphone, <see cref="WriteVideo"/> per camera, <see cref="SendPayload(string, string?, IReadOnlyDictionary{string, object?}?)"/> for domain hints;</item>
/// <item>tell it what is on air: <see cref="ReportOnAir"/> whenever the operator cuts;</item>
/// <item>react to <see cref="SuggestionReceived"/> (Suggest mode) or <see cref="OnAirChanged"/> (Auto mode).</item>
/// </list>
/// Thread-safe. Events are raised through <see cref="AiClientOptions.Dispatch"/> when set.
/// </summary>
public sealed class AiClient : IAsyncDisposable
{
    private readonly IAiProvider _provider;
    private readonly AiClientOptions _options;
    private readonly SessionClock _clock = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private AiSessionOptions? _session;
    private HashSet<string> _cameraIds = [];
    private DecisionArbiter? _arbiter;
    private AudioFeed? _audio;
    private volatile VideoFeed? _video;
    private string? _onAir;

    public AiClient(IAiProvider provider, AiClientOptions? options = null)
    {
        _provider = provider;
        _options = options ?? new AiClientOptions();
        _provider.StateChanged += OnProviderStateChanged;
        _provider.Log += line => Raise(LogReceived, new AiLogEntry(_provider.Name, line));
        _provider.SessionStarted += OnSessionStarted;
        _provider.MessageReceived += OnMessage;
    }

    /// <summary>Connection state changes (starting, running, faulted, restarting after a crash...).</summary>
    public event EventHandler<AiStateChange>? StateChanged;

    /// <summary>The worker accepted the session. Also raised after an automatic restart.</summary>
    public event EventHandler<WorkerCapabilities>? SessionStarted;

    /// <summary>Every shot suggested by the worker, with the arbiter verdict in Auto mode.</summary>
    public event EventHandler<AiSuggestion>? SuggestionReceived;

    /// <summary>
    /// What is on air changed: session start, accepted suggestion (Auto mode: the app must switch
    /// to <see cref="AiOnAirChange.CameraId"/>), or <see cref="ReportOnAir"/>.
    /// </summary>
    public event EventHandler<AiOnAirChange>? OnAirChanged;

    /// <summary>Periodic worker diagnostics (metrics declared in <see cref="WorkerCapabilities.Metrics"/>).</summary>
    public event EventHandler<WorkerStats>? StatsReceived;

    public event EventHandler<AiLogEntry>? LogReceived;

    public AiConnectionState State { get; private set; } = AiConnectionState.Stopped;

    /// <summary>Options of the running session, null when stopped.</summary>
    public AiSessionOptions? Session => _session;

    public bool IsRunning => _session is not null;

    /// <summary>Capabilities of the current worker, null until the session is accepted.</summary>
    public WorkerCapabilities? Capabilities { get; private set; }

    public string? OnAirCameraId
    {
        get
        {
            lock (_gate)
            {
                return _onAir;
            }
        }
    }

    /// <summary>Session clock (µs since <see cref="StartAsync"/>), the time base of every pts.</summary>
    public long NowUs => _clock.NowUs;

    public async Task StartAsync(AiSessionOptions session, CancellationToken cancellationToken = default)
    {
        var cameraIds = Validate(session);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A session is already running: call StopAsync or RestartAsync.");
            }

            var initial = session.InitialCameraId ?? session.Cameras[0].Id;
            _clock.Restart();
            lock (_gate)
            {
                _session = session;
                _cameraIds = cameraIds;
                _onAir = initial;
                _arbiter = session.Mode == AiDecisionMode.Auto ? new DecisionArbiter(session.Arbiter) : null;
                _arbiter?.Reset(initial, 0);
            }

            Raise(OnAirChanged, new AiOnAirChange(initial, AiOnAirSource.Initial, 0));

            var request = new AiProviderRequest(
                session.Domain,
                session.Cameras.Select(ToDescriptor).ToList(),
                session.AudioChannels,
                session.Parameters,
                () => _clock.NowUs);
            try
            {
                await _provider.StartAsync(request, cancellationToken);
            }
            catch
            {
                ClearSession();
                throw;
            }

            if (session.AudioChannels > 0)
            {
                _audio = new AudioFeed(session.AudioChannels, block => _provider.PushAudio(block, _clock.NowUs));
                _audio.Start();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_session is null)
            {
                return;
            }

            _audio?.Dispose();
            _audio = null;
            await _provider.StopAsync();
            ClearSession();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops and starts again, e.g. when the camera list changes. The worker starts from scratch.</summary>
    public async Task RestartAsync(AiSessionOptions session, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await StartAsync(session, cancellationToken);
    }

    /// <summary>
    /// The app put <paramref name="cameraId"/> on air (operator cut, applied suggestion in Suggest mode...).
    /// The worker needs it to reason about the current shot; in Auto mode it also locks AI cuts for
    /// <see cref="ArbiterOptions.ManualLockMs"/>.
    /// </summary>
    public void ReportOnAir(string cameraId)
    {
        var now = NowUs;
        lock (_gate)
        {
            if (_session is null || !_cameraIds.Contains(cameraId) || cameraId == _onAir)
            {
                return;
            }

            _onAir = cameraId;
            _arbiter?.TakeManual(cameraId, now);
        }

        SendOnAir(cameraId, now, manual: true);
        Raise(OnAirChanged, new AiOnAirChange(cameraId, AiOnAirSource.Manual, now));
    }

    /// <summary>
    /// Sends a domain payload (see <see cref="WorkerCapabilities.PayloadTypes"/>). <c>camera_id</c> is
    /// added when <paramref name="cameraId"/> is set. Values: null, bool, string or number.
    /// </summary>
    public bool SendPayload(string type, string? cameraId = null, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var data = new Struct();
        foreach (var (key, value) in fields ?? new Dictionary<string, object?>())
        {
            data.Fields[key] = ToValue(value);
        }

        if (cameraId is not null)
        {
            data.Fields["camera_id"] = Value.ForString(cameraId);
        }

        return SendPayload(type, data);
    }

    /// <summary>Sends a domain payload with an already built protobuf <see cref="Struct"/>.</summary>
    public bool SendPayload(string type, Struct data)
    {
        var sent = _provider.Send(new ClientMessage
        {
            Payload = new PayloadEvent { PtsUs = NowUs, Type = type, Data = data },
        });
        if (sent)
        {
            Raise(LogReceived, new AiLogEntry("client", $"payload {type} {data}"));
        }

        return sent;
    }

    /// <summary>
    /// Audio of one microphone (<see cref="AiCamera.AudioChannel"/>): mono float32 in [-1, 1], any sample
    /// rate, any block size, any thread. Resampled to 16 kHz and paced by the SDK. Ignored when stopped.
    /// </summary>
    public void WriteAudio(int channel, ReadOnlySpan<float> samples, int sampleRate) =>
        _audio?.Write(channel, samples, sampleRate);

    /// <summary>
    /// Video format the worker reads (<see cref="WriteVideo"/> scales to it), null when it takes no video
    /// or before the session is accepted. Producing frames at this size avoids the scaling cost.
    /// </summary>
    public VideoFormat? VideoFormat => _video?.Format;

    /// <summary>
    /// One frame of <paramref name="cameraId"/>: BGRA8, any size (<paramref name="stride"/> 0 = width * 4),
    /// any thread, one producer per camera. Scaled to <see cref="VideoFormat"/> and paced to its fps by the SDK.
    /// Returns false when dropped (too early, unknown camera, worker without video, no session).
    /// </summary>
    public bool WriteVideo(string cameraId, ReadOnlySpan<byte> bgra, int width, int height, int stride = 0) =>
        _video is { } video && video.Write(cameraId, bgra, width, height, stride, NowUs);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _provider.DisposeAsync();
        _lifecycle.Dispose();
    }

    private void ClearSession()
    {
        _clock.Stop();
        lock (_gate)
        {
            _session = null;
            _cameraIds = [];
            _arbiter = null;
        }

        Capabilities = null;
        _video = null;
    }

    private void OnProviderStateChanged(AiConnectionState state, string detail)
    {
        State = state;
        Raise(StateChanged, new AiStateChange(state, detail));
    }

    private void OnSessionStarted(WorkerCapabilities capabilities)
    {
        Capabilities = capabilities;
        _video = CreateVideoFeed(capabilities);

        // Also after a restart: resync the new worker with what is on air.
        if (OnAirCameraId is { } onAir)
        {
            SendOnAir(onAir, NowUs, manual: false);
        }

        Raise(SessionStarted, capabilities);
    }

    private VideoFeed? CreateVideoFeed(WorkerCapabilities capabilities)
    {
        try
        {
            if (VideoFeed.WantedFormat(capabilities) is not { } format)
            {
                return null;
            }

            HashSet<string> cameraIds;
            lock (_gate)
            {
                cameraIds = [.. _cameraIds];
            }

            return new VideoFeed(format, cameraIds, (cameraId, pixels, ptsUs) => _provider.PushVideo(cameraId, pixels, ptsUs));
        }
        catch (NotSupportedException ex)
        {
            Raise(LogReceived, new AiLogEntry("client", $"No video: {ex.Message}"));
            return null;
        }
    }

    private void OnMessage(WorkerMessage message)
    {
        switch (message.BodyCase)
        {
            case WorkerMessage.BodyOneofCase.Decision:
                OnDecision(message.Decision);
                break;

            case WorkerMessage.BodyOneofCase.Stats:
                Raise(StatsReceived, message.Stats);
                break;

            case WorkerMessage.BodyOneofCase.Log:
                Raise(LogReceived, new AiLogEntry("worker", $"{message.Log.Level}: {message.Log.Message}"));
                break;

            case WorkerMessage.BodyOneofCase.Error:
                Raise(LogReceived, new AiLogEntry("worker",
                    $"ERROR {message.Error.Code}: {message.Error.Message} (fatal={message.Error.Fatal})"));
                break;

            case WorkerMessage.BodyOneofCase.Ended:
                Raise(LogReceived, new AiLogEntry("worker", $"Session ended: {message.Ended.Reason}"));
                break;
        }
    }

    private void OnDecision(SceneDecision decision)
    {
        var now = NowUs;
        AiVerdict? verdict = null;
        lock (_gate)
        {
            if (_arbiter is { } arbiter)
            {
                verdict = arbiter.Evaluate(decision, now);
                if (verdict.Accepted)
                {
                    _onAir = decision.CameraId;
                }
            }
        }

        Raise(SuggestionReceived, ToSuggestion(decision, now, verdict));

        if (verdict?.Accepted == true)
        {
            SendOnAir(decision.CameraId, now, manual: false);
            Raise(OnAirChanged, new AiOnAirChange(decision.CameraId, AiOnAirSource.Ai, now));
        }
    }

    private void SendOnAir(string cameraId, long nowUs, bool manual) =>
        _provider.Send(new ClientMessage
        {
            OnAir = new CameraOnAir { CameraId = cameraId, PtsUs = nowUs, Manual = manual },
        });

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        if (_options.Dispatch is { } dispatch)
        {
            dispatch(() => handler(this, args));
        }
        else
        {
            handler(this, args);
        }
    }

    private static HashSet<string> Validate(AiSessionOptions session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Domain);
        if (session.Cameras.Count == 0)
        {
            throw new ArgumentException("At least one camera is required.", nameof(session));
        }

        var ids = new HashSet<string>();
        foreach (var camera in session.Cameras)
        {
            if (string.IsNullOrWhiteSpace(camera.Id) || !ids.Add(camera.Id))
            {
                throw new ArgumentException($"Camera ids must be unique and non-empty ('{camera.Id}').", nameof(session));
            }
        }

        if (session.InitialCameraId is { } initial && !ids.Contains(initial))
        {
            throw new ArgumentException($"Initial camera '{initial}' is not in the camera list.", nameof(session));
        }

        return ids;
    }

    private static CameraDescriptor ToDescriptor(AiCamera camera) => new()
    {
        CameraId = camera.Id,
        Label = camera.Label,
        Role = camera.Role,
        AudioChannel = camera.AudioChannel,
    };

    private static AiSuggestion ToSuggestion(SceneDecision decision, long nowUs, AiVerdict? verdict) => new()
    {
        DecisionId = decision.DecisionId,
        CameraId = decision.CameraId,
        SceneId = string.IsNullOrEmpty(decision.SceneId) ? null : decision.SceneId,
        Confidence = decision.Confidence,
        Reason = decision.Reason,
        Transition = new AiTransition(
            decision.Transition?.Kind == Transition.Types.Kind.Dissolve ? AiTransitionKind.Dissolve : AiTransitionKind.Cut,
            decision.Transition?.DurationMs ?? 0),
        MinHoldMs = decision.MinHoldMs,
        SourcePtsUs = decision.SourcePtsUs,
        ReceivedPtsUs = nowUs,
        ProcessingMs = decision.ProcessingMs,
        Scores = decision.Scores.ToDictionary(s => s.CameraId, s => s.Score),
        Verdict = verdict,
    };

    private static Value ToValue(object? value) => value switch
    {
        null => Value.ForNull(),
        bool b => Value.ForBool(b),
        string s => Value.ForString(s),
        int or long or float or double or decimal or short or byte or uint or ulong => Value.ForNumber(Convert.ToDouble(value)),
        System.Enum e => Value.ForString(e.ToString()),
        _ => Value.ForString(value.ToString() ?? ""),
    };
}
