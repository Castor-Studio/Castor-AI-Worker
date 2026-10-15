using System.Globalization;

namespace Castor.AI.Worker.Strategies.Podcast;

/// <summary>Per-camera hints sent by the app (<c>camera.metadata</c> / <c>speaker.*</c> payloads).</summary>
public sealed record CameraMetadata(
    bool? IsSpeaking = null,
    double? VolumeDb = null,
    bool? FaceDetected = null,
    bool? PersonDetected = null)
{
    public static CameraMetadata Empty { get; } = new();
}

/// <summary>What the director knows about one speaker role at a tick.</summary>
public readonly record struct SpeakerSignal(bool AudioSpeaking, double AudioDb, CameraMetadata Metadata)
{
    public static SpeakerSignal Silent { get; } = new(false, -100.0, CameraMetadata.Empty);
}

public sealed record DirectorTick(
    IReadOnlyList<string> Active,
    IReadOnlyList<string> Gated,
    IReadOnlyList<string> Confirmed,
    IReadOnlyDictionary<string, double> Volumes,
    string? TargetRole,
    string? DecisionCameraId,
    string Reason,
    double HoldRemaining);

/// <summary>
/// Shot selection state machine, port of the Python <c>PodcastModule._analyze_sync</c>
/// (Castor-AI, podcast director engine v2):
/// <list type="number">
/// <item>active speakers = (metadata is_speaking OR mic VAD) AND not an empty chair;</item>
/// <item>cross-gating: drop speakers quieter than the loudest by <c>cross_gating_threshold_db</c> (mic bleed);</item>
/// <item>backchannel filter: keep speakers talking for at least <c>min_speech_confirm_ms</c>;</item>
/// <item>state machine: silence / debate / single speaker (+ monologue zoom, ping-pong governor);</item>
/// <item>anti-flicker: no cut before <c>min_hold_time</c>.</item>
/// </list>
/// One difference with Python: the current shot comes from the app (<see cref="OnAir"/>),
/// not from the last decision, because the app arbiter or the operator may override it.
/// Times are seconds on the session clock.
/// </summary>
public sealed class PodcastDirector(
    PodcastOptions options,
    IReadOnlyDictionary<string, string> roles,
    string? fallbackCameraId)
{
    /// <summary>Min delay before suggesting the same camera again when the app did not take it.</summary>
    public const double ResuggestSeconds = 1.0;

    private readonly Dictionary<string, double> _speakerActiveSince = [];
    private readonly List<(double Time, string Role)> _speakerSwitches = [];
    private readonly Dictionary<string, double> _speakingSince = [];

    private string? _currentCameraId;
    private bool _hasOnAir;
    private double? _lastSwitchTime;
    private string? _lastActiveSpeaker;
    private double? _silenceStartTime;
    private double? _debateStartTime;
    private string? _lastEmittedCamera;
    private double _lastEmittedAt;
    private string _reason = "";

    public string? CurrentCameraId => _currentCameraId;

    public IReadOnlyDictionary<string, string> Roles => roles;

    /// <summary>Feedback from the app: this camera is now on air.</summary>
    public void OnAir(string cameraId, double now)
    {
        if (cameraId == _currentCameraId)
        {
            return;
        }

        // The first shot of the session is not a cut; anything after it (our decision applied,
        // arbiter, manual take) restarts the hold window.
        var initial = !_hasOnAir && cameraId != _lastEmittedCamera;
        _hasOnAir = true;
        _currentCameraId = cameraId;
        if (!initial)
        {
            _lastSwitchTime = now;
        }
    }

    public DirectorTick Tick(IReadOnlyDictionary<string, SpeakerSignal> signals, double now)
    {
        // 1. Active speakers: metadata first, VAD fallback, visual presence veto.
        var active = new List<string>();
        var volumes = new Dictionary<string, double>();
        foreach (var role in roles.Keys)
        {
            if (!PodcastRoles.IsSpeakerRole(role))
            {
                continue;
            }

            var signal = signals.TryGetValue(role, out var s) ? s : SpeakerSignal.Silent;
            var metadata = signal.Metadata;
            var volume = metadata.VolumeDb ?? signal.AudioDb;

            // person_detected wins over face_detected (profile view = face lost, person still there).
            var emptyChair = metadata.PersonDetected switch
            {
                true => false,
                false => true,
                null => metadata.FaceDetected == false,
            };

            volumes[role] = volume;
            if ((metadata.IsSpeaking == true || signal.AudioSpeaking) && !emptyChair)
            {
                active.Add(role);
            }
        }

        // 2. Cross-gating, 3. backchannel filter, 4. state machine.
        var gated = ApplyCrossGating(active, volumes);
        var confirmed = ConfirmSpeakers(gated, now);
        var targetRole = RunStateMachine(confirmed, now);
        var holdRemaining = _currentCameraId is not null && _lastSwitchTime is double last
            ? Math.Max(0, options.MinHoldTime - (now - last))
            : 0;

        string? decision = null;
        if (targetRole is not null)
        {
            var targetCamera = roles.TryGetValue(targetRole, out var id) ? id : fallbackCameraId;
            var resuggestBlocked = targetCamera == _lastEmittedCamera && now - _lastEmittedAt < ResuggestSeconds;

            // 5. Anti-flicker guard: gate emitting the switch, not tracking it.
            if (targetCamera is not null && targetCamera != _currentCameraId && holdRemaining <= 0 && !resuggestBlocked)
            {
                decision = targetCamera;
                _lastEmittedCamera = targetCamera;
                _lastEmittedAt = now;
            }
        }

        return new DirectorTick(active, gated, confirmed, volumes, targetRole, decision, _reason, holdRemaining);
    }

    private List<string> ApplyCrossGating(List<string> active, Dictionary<string, double> volumes)
    {
        if (active.Count <= 1)
        {
            return active;
        }

        var loudest = active[0];
        foreach (var role in active)
        {
            if (volumes[role] > volumes[loudest])
            {
                loudest = role;
            }
        }

        var maxVolume = volumes[loudest];
        return active.Where(role => maxVolume - volumes[role] < options.CrossGatingThresholdDb).ToList();
    }

    private List<string> ConfirmSpeakers(List<string> rawActive, double now)
    {
        var rawSet = rawActive.ToHashSet();
        foreach (var role in _speakingSince.Keys.ToList())
        {
            if (!rawSet.Contains(role))
            {
                _speakingSince.Remove(role);
            }
        }

        foreach (var role in rawActive)
        {
            _speakingSince.TryAdd(role, now);
        }

        var threshold = options.MinSpeechConfirmMs / 1000.0;
        return rawActive.Where(role => now - _speakingSince[role] >= threshold).ToList();
    }

    private string? RunStateMachine(List<string> activeSpeakers, double now)
    {
        var speakers = activeSpeakers.Distinct().Order(StringComparer.Ordinal).ToList();

        // Case 1: silence / natural pause.
        if (speakers.Count == 0)
        {
            _debateStartTime = null;
            _silenceStartTime ??= now;
            var silence = now - _silenceStartTime.Value;

            if (silence >= options.SilenceHoldTime)
            {
                _speakerActiveSince.Clear();
                _lastActiveSpeaker = null;
                _reason = Format($"Silence for {silence:0.0} s, wide shot");
                return PodcastRoles.Wide;
            }

            if (_lastActiveSpeaker is not null)
            {
                var activeDuration = now - _speakerActiveSince.GetValueOrDefault(_lastActiveSpeaker, now);
                if (HasZoom(_lastActiveSpeaker) && activeDuration >= options.MonologueTime)
                {
                    _reason = Format($"Pause in {_lastActiveSpeaker} monologue, hold zoom");
                    return _lastActiveSpeaker + PodcastRoles.ZoomSuffix;
                }

                _reason = Format($"Natural pause ({silence:0.0} s), hold {_lastActiveSpeaker}");
                return _lastActiveSpeaker;
            }

            _reason = "Silence";
            return null;
        }

        _silenceStartTime = null;

        // Case 2: debate, several sustained speakers.
        if (speakers.Count > 1)
        {
            _debateStartTime ??= now;
            var debate = now - _debateStartTime.Value;

            if (debate >= options.DebateConfirmMs / 1000.0)
            {
                _speakerActiveSince.Clear();
                _lastActiveSpeaker = null;
                _reason = Format($"Debate {string.Join("+", speakers)} for {debate:0.0} s, wide shot");
                return PodcastRoles.Wide;
            }

            if (_lastActiveSpeaker is not null && speakers.Contains(_lastActiveSpeaker))
            {
                _reason = Format($"Debate not confirmed yet, hold {_lastActiveSpeaker}");
                return _lastActiveSpeaker;
            }

            _reason = Format($"Debate not confirmed yet, {speakers[0]}");
            return speakers[0];
        }

        // Case 3: single speaker.
        _debateStartTime = null;
        var speaker = speakers[0];

        if (speaker != _lastActiveSpeaker)
        {
            if (_lastActiveSpeaker is not null)
            {
                _speakerSwitches.Add((now, speaker));
                var cutoff = now - options.PingPongWindowSec;
                _speakerSwitches.RemoveAll(s => s.Time < cutoff);
            }

            _speakerActiveSince.Clear();
            _speakerActiveSince[speaker] = now;
            _lastActiveSpeaker = speaker;
        }

        // Ping-pong governor: rapid alternation within the window, keep the broadcast steady.
        if (options.PingPongThreshold > 0 && _speakerSwitches.Count >= options.PingPongThreshold)
        {
            _reason = Format($"Ping-pong: {_speakerSwitches.Count} switches in {options.PingPongWindowSec:0} s, wide shot");
            return PodcastRoles.Wide;
        }

        var duration = now - _speakerActiveSince.GetValueOrDefault(speaker, now);
        if (HasZoom(speaker) && duration >= options.MonologueTime)
        {
            _reason = Format($"{speaker} monologue for {duration:0.0} s, zoom");
            return speaker + PodcastRoles.ZoomSuffix;
        }

        _reason = Format($"{speaker} speaking for {duration:0.0} s");
        return speaker;
    }

    private bool HasZoom(string speaker) => roles.ContainsKey(speaker + PodcastRoles.ZoomSuffix);

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
