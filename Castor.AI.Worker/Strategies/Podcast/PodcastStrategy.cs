using System.Globalization;
using Castor.AI.Contracts;
using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Audio;
using Google.Protobuf.WellKnownTypes;

namespace Castor.AI.Worker.Strategies.Podcast;

/// <summary>
/// Podcast worker: one Silero VAD per microphone (audio ring channel) feeding the
/// <see cref="PodcastDirector"/>. App payloads (<c>camera.metadata</c>, <c>speaker.*</c>)
/// are merged per camera and act as the Python "client metadata", OR-ed with the VAD.
/// </summary>
public sealed class PodcastStrategy(SileroVadModel vadModel) : IDomainStrategy
{
    public const string Domain = "podcast";

    private const int ChunkFrames = SileroVadModel.ChunkSamples;
    private const float DecisionConfidence = 0.9f;

    // WorkerStats keys, described in Describe().
    private const string MicLevelMetric = "mic_db";
    private const string SpeechProbabilityMetric = "speech_prob";
    private const string ConfirmedSpeakersMetric = "confirmed_speakers";
    private const string HoldRemainingMetric = "hold_remaining_s";
    private const string VadFlag = "vad";
    private const string ActiveFlag = "active";
    private const string GatedFlag = "gated";
    private const string ConfirmedFlag = "confirmed";
    private const string EmptyChairFlag = "empty_chair";

    private readonly Dictionary<string, CameraDescriptor> _cameras = [];
    private readonly Dictionary<string, CameraMetadata> _metadata = [];
    private readonly Dictionary<int, SpeechActivityTracker> _trackers = [];
    private readonly Dictionary<string, int> _speakerChannels = [];

    private PodcastOptions _options = new();
    private PodcastDirector? _director;
    private AudioRingReader? _audio;
    private float[] _block = [];
    private float[] _chunk = new float[ChunkFrames];
    private long _readFrame;
    private long _framesSkipped;
    private DirectorTick? _lastTick;

    public static WorkerCapabilities Describe(string workerId) => new()
    {
        WorkerId = workerId,
        Domain = Domain,
        Version = "0.2.0",
        OptionalInputs = { InputKind.Audio, InputKind.Payload },
        Transports = { TransportKind.SharedMemory },
        MaxCameras = 8,
        PreferredAudio = new AudioFormat
        {
            SampleRate = SileroVadModel.SampleRate,
            Channels = 1, // per microphone; the ring holds one channel per mic.
            SampleFormat = SampleFormat.F32Interleaved,
        },
        PayloadTypes = { PayloadTypes.SpeakerStart, PayloadTypes.SpeakerStop, PayloadTypes.CameraMetadata },
        Metrics =
        {
            CameraMetric(MicLevelMetric, "Mic level", "dB", -60, 0),
            CameraMetric(SpeechProbabilityMetric, "Speech probability", "", 0, 1),
            SessionMetric(ConfirmedSpeakersMetric, "Confirmed speakers", "", 0, 4),
            SessionMetric(HoldRemainingMetric, "Hold remaining", "s", 0, 5),
        },
        CameraFlags =
        {
            Flag(VadFlag, "VAD", "Silero detects speech on this camera's mic"),
            Flag(ActiveFlag, "ACTIVE", "Speaking (metadata or VAD), not an empty chair"),
            Flag(GatedFlag, "GATED", "Kept by cross-gating (not mic bleed)"),
            Flag(ConfirmedFlag, "CONFIRMED", "Speaking long enough to count (backchannel filter)"),
            Flag(EmptyChairFlag, "EMPTY", "Visual metadata says nobody is there: never cut here"),
        },
    };

    public void Start(SessionStart start, SessionMedia media, Action<string> log)
    {
        _options = PodcastOptions.FromParams(start.Params);
        foreach (var camera in start.Cameras)
        {
            _cameras[camera.CameraId] = camera;
        }

        var roles = PodcastRoles.Resolve(start.Cameras);
        _director = new PodcastDirector(_options, roles, start.Cameras[0].CameraId);

        log($"Podcast config: {_options}");
        log("Roles: " + string.Join(", ", roles.Select(r => $"{r.Key}={Label(r.Value)}")));

        _audio = media.Audio;
        if (_audio is null)
        {
            log("No audio transport: decisions rely on payload metadata only");
            return;
        }

        if (_audio.SampleRate != SileroVadModel.SampleRate)
        {
            throw new InvalidOperationException(
                $"Audio ring at {_audio.SampleRate} Hz, Silero VAD needs {SileroVadModel.SampleRate} Hz");
        }

        foreach (var (role, cameraId) in roles)
        {
            var channel = _cameras[cameraId].AudioChannel;
            if (!PodcastRoles.IsSpeakerRole(role) || channel < 0 || channel >= _audio.Channels)
            {
                continue;
            }

            _speakerChannels[role] = channel;
            if (!_trackers.ContainsKey(channel))
            {
                var iterator = new VadIterator(
                    vadModel.CreateStream(),
                    _options.VadThreshold,
                    SileroVadModel.SampleRate,
                    _options.MinSilenceDurationMs,
                    _options.SpeechPadMs);
                _trackers[channel] = new SpeechActivityTracker(iterator, _options.LevelSmoothingMs);
            }
        }

        _block = new float[ChunkFrames * _audio.Channels];
        _readFrame = _audio.WriteFrame;
        log($"Audio: {_audio.Channels} channels @ {_audio.SampleRate} Hz, VAD on " +
            string.Join(", ", _speakerChannels.Select(s => $"{s.Key}=ch{s.Value}")));
    }

    public void OnPayload(PayloadEvent payload)
    {
        var fields = payload.Data?.Fields;
        if (fields is null
            || !fields.TryGetValue("camera_id", out var idValue)
            || !_cameras.ContainsKey(idValue.StringValue))
        {
            return;
        }

        var cameraId = idValue.StringValue;
        var current = _metadata.GetValueOrDefault(cameraId, CameraMetadata.Empty);
        _metadata[cameraId] = payload.Type switch
        {
            PayloadTypes.SpeakerStart => current with { IsSpeaking = true },
            PayloadTypes.SpeakerStop => current with { IsSpeaking = false },
            PayloadTypes.CameraMetadata => current with
            {
                IsSpeaking = Merge(fields, "is_speaking", current.IsSpeaking, AsBool),
                VolumeDb = Merge(fields, "volume_db", current.VolumeDb, AsDouble),
                FaceDetected = Merge(fields, "face_detected", current.FaceDetected, AsBool),
                PersonDetected = Merge(fields, "person_detected", current.PersonDetected, AsBool),
            },
            _ => current,
        };
    }

    public void OnAir(CameraOnAir onAir) => _director?.OnAir(onAir.CameraId, Seconds(onAir.PtsUs));

    public SceneDecision? Evaluate(long nowUs)
    {
        if (_director is null)
        {
            return null;
        }

        ProcessAudio();

        var signals = new Dictionary<string, SpeakerSignal>();
        foreach (var (role, cameraId) in _director.Roles)
        {
            if (!PodcastRoles.IsSpeakerRole(role))
            {
                continue;
            }

            var tracker = _speakerChannels.TryGetValue(role, out var channel) ? _trackers[channel] : null;
            signals[role] = new SpeakerSignal(
                tracker?.IsSpeaking ?? false,
                tracker?.LevelDb ?? -100.0,
                _metadata.GetValueOrDefault(cameraId, CameraMetadata.Empty));
        }

        var tick = _director.Tick(signals, Seconds(nowUs));
        _lastTick = tick;
        if (tick.DecisionCameraId is not { } target)
        {
            return null;
        }

        var decision = new SceneDecision
        {
            DecisionId = Guid.NewGuid().ToString("N")[..8],
            SourcePtsUs = nowUs,
            CameraId = target,
            Confidence = DecisionConfidence,
            Transition = new Transition { Kind = Transition.Types.Kind.Cut },
            MinHoldMs = (uint)(_options.MinHoldTime * 1000),
            Reason = tick.Reason,
        };

        foreach (var (role, cameraId) in _director.Roles)
        {
            float score = 0;
            if (PodcastRoles.IsSpeakerRole(role))
            {
                score = tick.Confirmed.Contains(role) ? 1f
                    : tick.Gated.Contains(role) ? 0.6f
                    : tick.Active.Contains(role) ? 0.3f
                    : 0f;
            }

            if (cameraId == target)
            {
                score = 1f;
            }

            decision.Scores.Add(new CameraScore { CameraId = cameraId, Score = score });
        }

        return decision;
    }

    public void FillStats(WorkerStats stats)
    {
        stats.FramesDropped = (ulong)_framesSkipped;
        if (_director is null)
        {
            return;
        }

        var tick = _lastTick;
        foreach (var (role, cameraId) in _director.Roles)
        {
            if (!PodcastRoles.IsSpeakerRole(role))
            {
                continue;
            }

            var camera = new CameraStats();
            if (_speakerChannels.TryGetValue(role, out var channel))
            {
                var tracker = _trackers[channel];
                camera.Metrics[MicLevelMetric] = tracker.LevelDb;
                camera.Metrics[SpeechProbabilityMetric] = tracker.LastProbability;
                if (tracker.IsSpeaking)
                {
                    camera.Flags.Add(VadFlag);
                }
            }

            if (tick is not null)
            {
                AddFlagIf(camera, ActiveFlag, tick.Active.Contains(role));
                AddFlagIf(camera, GatedFlag, tick.Gated.Contains(role));
                AddFlagIf(camera, ConfirmedFlag, tick.Confirmed.Contains(role));
            }

            var metadata = _metadata.GetValueOrDefault(cameraId, CameraMetadata.Empty);
            AddFlagIf(camera, EmptyChairFlag,
                metadata.PersonDetected == false || (metadata.PersonDetected is null && metadata.FaceDetected == false));

            stats.Cameras[cameraId] = camera;
        }

        if (tick is not null)
        {
            stats.Metrics[ConfirmedSpeakersMetric] = tick.Confirmed.Count;
            stats.Metrics[HoldRemainingMetric] = tick.HoldRemaining;
            stats.Detail = string.Create(CultureInfo.InvariantCulture,
                $"target={tick.TargetRole ?? "-"} | {tick.Reason}");
        }
    }

    public void Dispose()
    {
        // The audio ring belongs to SessionMedia; the VAD model is shared.
    }

    /// <summary>Runs the VAD on every complete 512-frame block written since the last call.</summary>
    private void ProcessAudio()
    {
        if (_audio is null || _trackers.Count == 0)
        {
            return;
        }

        var channels = _audio.Channels;
        var written = _audio.WriteFrame;
        if (written - _readFrame > _audio.CapacityFrames)
        {
            // Fell behind the writer: jump to the newest data (latest-wins).
            var resume = written - ChunkFrames;
            _framesSkipped += resume - _readFrame;
            _readFrame = resume;
        }

        while (written - _readFrame >= ChunkFrames)
        {
            if (!_audio.TryRead(_readFrame, ChunkFrames, _block))
            {
                _readFrame = _audio.WriteFrame - ChunkFrames;
                written = _audio.WriteFrame;
                continue;
            }

            foreach (var (channel, tracker) in _trackers)
            {
                for (var i = 0; i < ChunkFrames; i++)
                {
                    _chunk[i] = _block[i * channels + channel];
                }

                tracker.Process(_chunk);
            }

            _readFrame += ChunkFrames;
        }
    }

    private static MetricDescriptor CameraMetric(string key, string label, string unit, double min, double max) =>
        new() { Key = key, Label = label, Unit = unit, Min = min, Max = max, Scope = MetricDescriptor.Types.Scope.Camera };

    private static MetricDescriptor SessionMetric(string key, string label, string unit, double min, double max) =>
        new() { Key = key, Label = label, Unit = unit, Min = min, Max = max, Scope = MetricDescriptor.Types.Scope.Session };

    private static FlagDescriptor Flag(string key, string label, string description) =>
        new() { Key = key, Label = label, Description = description };

    private static void AddFlagIf(CameraStats camera, string flag, bool raised)
    {
        if (raised)
        {
            camera.Flags.Add(flag);
        }
    }

    private string Label(string cameraId) =>
        _cameras.TryGetValue(cameraId, out var camera) && camera.Label.Length > 0 ? camera.Label : cameraId;

    private static double Seconds(long us) => us / 1_000_000.0;

    private static T? Merge<T>(IDictionary<string, Value> fields, string key, T? current, Func<Value, T?> parse)
        where T : struct =>
        fields.TryGetValue(key, out var value) ? parse(value) : current;

    private static bool? AsBool(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.BoolValue => value.BoolValue,
        Value.KindOneofCase.NumberValue => value.NumberValue != 0,
        Value.KindOneofCase.StringValue => value.StringValue.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null,
        },
        _ => null,
    };

    private static double? AsDouble(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.NumberValue => value.NumberValue,
        Value.KindOneofCase.StringValue when double.TryParse(
            value.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
