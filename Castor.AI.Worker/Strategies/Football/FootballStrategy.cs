using System.Diagnostics;
using System.Globalization;
using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker.Strategies.Football;

/// <summary>
/// Football worker, port of Castor-AI <c>castostudio_ai_football</c>: ball detection (YOLO) on the
/// latest frame of every camera, then <see cref="FootballFocus"/> picks the camera that alone sees the ball.
/// </summary>
public sealed class FootballStrategy(IBallModel model) : IDomainStrategy
{
    public const string Domain = "football";

    // Python reads the full stream and resizes to 416x416; 640x360 keeps the cost of the copy low.
    private const uint VideoWidth = 640;
    private const uint VideoHeight = 360;
    private const double VideoFps = 15;

    // WorkerStats keys, described in Describe().
    private const string BallConfidenceMetric = "ball_conf";
    private const string DetectMsMetric = "detect_ms";
    private const string BallFlag = "ball";
    private const string FocusFlag = "focus";

    private readonly Dictionary<string, CameraState> _cameras = [];
    private FootballOptions _options = new();
    private BallDetector? _detector;
    private FootballFocus? _focus;
    private long _rounds;
    private long _framesSkipped;
    private long _statsTimestamp = Stopwatch.GetTimestamp();

    public static WorkerCapabilities Describe(string workerId) => new()
    {
        WorkerId = workerId,
        Domain = Domain,
        Version = "0.1.0",
        RequiredInputs = { InputKind.Video },
        Transports = { TransportKind.SharedMemory },
        MaxCameras = 4,
        PreferredVideo = new VideoFormat
        {
            Width = VideoWidth,
            Height = VideoHeight,
            PixelFormat = PixelFormat.Bgra8,
            Fps = VideoFps,
        },
        Metrics =
        {
            CameraMetric(BallConfidenceMetric, "Ball confidence", "", 0, 1),
            CameraMetric(DetectMsMetric, "Detection time", "ms", 0, 200),
        },
        CameraFlags =
        {
            Flag(BallFlag, "BALL", "Ball detected on the latest frame (score >= ball_confidence)"),
            Flag(FocusFlag, "FOCUS", "Current focus: last camera that alone saw the ball"),
        },
    };

    public void Start(SessionStart start, SessionMedia media, Action<string> log)
    {
        _options = FootballOptions.FromParams(start.Params);
        _detector = new BallDetector(model, _options.Detector);
        foreach (var camera in start.Cameras)
        {
            if (!media.Video.TryGetValue(camera.CameraId, out var reader))
            {
                throw new InvalidOperationException($"Football needs video on every camera: none for '{camera.CameraId}'");
            }

            if (reader.PixelFormat != PixelFormat.Bgra8)
            {
                throw new InvalidOperationException($"Camera '{camera.CameraId}': {reader.PixelFormat} video, BGRA8 expected");
            }

            _cameras[camera.CameraId] = new CameraState(camera, reader);
        }

        _focus = new FootballFocus(start.Cameras.Select(c => c.CameraId).ToList());
        log($"Football config: {_options}");
        log($"Video: {string.Join(", ", _cameras.Values.Select(c => $"{c.Label}={c.Reader.Width}x{c.Reader.Height}"))}, " +
            $"model input {model.InputSize}x{model.InputSize}");
    }

    public void OnPayload(PayloadEvent payload)
    {
        // No football payload yet.
    }

    public void OnAir(CameraOnAir onAir) => _focus?.OnAir(onAir.CameraId);

    public SceneDecision? Evaluate(long nowUs)
    {
        if (_detector is null || _focus is null)
        {
            return null;
        }

        // A round = the latest frame of every camera that has a new one; others keep their last result.
        var fresh = _cameras.Values.Where(c => c.Reader.LatestSeq > c.Seq).ToList();
        if (fresh.Count == 0)
        {
            return null;
        }

        var analyse = _rounds++ % (_options.Frameskip + 1) == 0;
        long newestPts = 0;
        var analysed = false;
        foreach (var camera in fresh)
        {
            if (!camera.Reader.TryReadLatest(camera.Seq, camera.Buffer, out var frame))
            {
                continue;
            }

            if (camera.Seq > 0 && frame.Seq > camera.Seq + 1)
            {
                _framesSkipped += (long)(frame.Seq - camera.Seq - 1);
            }

            camera.Seq = frame.Seq;
            camera.PtsUs = frame.PtsUs;
            if (!analyse)
            {
                _framesSkipped++;
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            camera.Detection = _detector.Detect(camera.Buffer, camera.Reader.Width, camera.Reader.Height, camera.Reader.Stride);
            camera.DetectMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            camera.Analysed++;
            newestPts = Math.Max(newestPts, frame.PtsUs);
            analysed = true;
            _focus.Report(camera.Id, camera.Detection.Value.Found);
        }

        if (!analysed || _focus.Decide(nowUs / 1_000_000.0) is not { } target)
        {
            return null;
        }

        var chosen = _cameras[target];
        var decision = new SceneDecision
        {
            DecisionId = Guid.NewGuid().ToString("N")[..8],
            SourcePtsUs = newestPts,
            CameraId = target,
            Confidence = _options.DefaultConfidence,
            Transition = new Transition { Kind = Transition.Types.Kind.Cut },
            MinHoldMs = _options.MinHoldMs,
            Reason = string.Create(CultureInfo.InvariantCulture,
                $"Ball only on {chosen.Label} ({chosen.Detection?.Confidence ?? 0:0.00})"),
        };

        foreach (var camera in _cameras.Values)
        {
            decision.Scores.Add(new CameraScore { CameraId = camera.Id, Score = camera.Detection?.Confidence ?? 0 });
        }

        return decision;
    }

    public void FillStats(WorkerStats stats)
    {
        var elapsed = Stopwatch.GetElapsedTime(_statsTimestamp).TotalSeconds;
        _statsTimestamp = Stopwatch.GetTimestamp();

        stats.FramesDropped = (ulong)_framesSkipped;
        foreach (var camera in _cameras.Values)
        {
            stats.FramesTorn += (ulong)camera.Reader.FramesTorn;
            stats.FpsPerCamera[camera.Id] = elapsed > 0 ? (float)(camera.Analysed / elapsed) : 0;
            camera.Analysed = 0;

            var cameraStats = new CameraStats();
            if (camera.Detection is { } detection)
            {
                cameraStats.Metrics[BallConfidenceMetric] = detection.Confidence;
                cameraStats.Metrics[DetectMsMetric] = camera.DetectMs;
                if (detection.Found)
                {
                    cameraStats.Flags.Add(BallFlag);
                }
            }

            if (_focus?.Focus == camera.Id)
            {
                cameraStats.Flags.Add(FocusFlag);
            }

            stats.Cameras[camera.Id] = cameraStats;
        }

        if (_focus is not null)
        {
            var focus = _focus.Focus is { } id ? _cameras[id].Label : "-";
            stats.Detail = $"focus={focus} | {_focus.Reason}";
        }
    }

    public void Dispose()
    {
        // The video regions belong to SessionMedia; the model is shared.
    }

    private static MetricDescriptor CameraMetric(string key, string label, string unit, double min, double max) =>
        new() { Key = key, Label = label, Unit = unit, Min = min, Max = max, Scope = MetricDescriptor.Types.Scope.Camera };

    private static FlagDescriptor Flag(string key, string label, string description) =>
        new() { Key = key, Label = label, Description = description };

    private sealed class CameraState(CameraDescriptor camera, VideoRingReader reader)
    {
        public string Id { get; } = camera.CameraId;

        public string Label { get; } = camera.Label.Length > 0 ? camera.Label : camera.CameraId;

        public VideoRingReader Reader { get; } = reader;

        public byte[] Buffer { get; } = new byte[reader.FrameSize];

        public ulong Seq { get; set; }

        public long PtsUs { get; set; }

        public BallDetection? Detection { get; set; }

        public double DetectMs { get; set; }

        public long Analysed { get; set; }
    }
}
