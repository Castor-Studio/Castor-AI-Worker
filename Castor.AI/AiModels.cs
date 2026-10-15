using Castor.AI.Providers;

namespace Castor.AI;

/// <summary>A camera (Castor Studio: a scene) the AI can choose.</summary>
/// <param name="Id">Stable id, echoed back in suggestions (e.g. the scene Guid as "N").</param>
/// <param name="Label">Human readable name. Some workers also read roles from it.</param>
/// <param name="Role">Domain role hint (podcast: host, guest, guest2, wide, host_zoom...). Empty: worker decides.</param>
/// <param name="AudioChannel">Microphone of this camera in <see cref="AiClient.WriteAudio"/>, -1 = none.</param>
public sealed record AiCamera(string Id, string Label, string Role = "", int AudioChannel = -1);

public enum AiDecisionMode
{
    /// <summary>Suggestions only: the app shows them and the operator decides (Castor Studio "agent").</summary>
    Suggest,

    /// <summary>The SDK arbiter puts accepted suggestions on air (<see cref="AiClient.OnAirChanged"/>, Castor Studio "auto").</summary>
    Auto,
}

/// <summary>Arbiter rules, used in <see cref="AiDecisionMode.Auto"/>.</summary>
public sealed record ArbiterOptions
{
    public float MinConfidence { get; init; } = 0.5f;

    /// <summary>Suggestions older than this when received are dropped.</summary>
    public int MaxDecisionAgeMs { get; init; } = 500;

    /// <summary>How long a manual take (<see cref="AiClient.ReportOnAir"/>) blocks AI cuts.</summary>
    public int ManualLockMs { get; init; } = 5_000;

    /// <summary>Min time on a shot when the suggestion has no min_hold_ms.</summary>
    public uint DefaultMinHoldMs { get; init; } = 1_500;
}

public sealed record AiSessionOptions
{
    /// <summary>Worker domain: "podcast", "football", ...</summary>
    public required string Domain { get; init; }

    public required IReadOnlyList<AiCamera> Cameras { get; init; }

    /// <summary>Domain tuning, forwarded as SessionStart.params (podcast: min_hold_time, ...).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public AiDecisionMode Mode { get; init; } = AiDecisionMode.Auto;

    public ArbiterOptions Arbiter { get; init; } = new();

    /// <summary>Camera on air when the session starts. Default: first camera.</summary>
    public string? InitialCameraId { get; init; }

    /// <summary>Audio channels to open: highest <see cref="AiCamera.AudioChannel"/> + 1.</summary>
    public int AudioChannels => Cameras.Count == 0 ? 0 : Math.Max(0, Cameras.Max(c => c.AudioChannel) + 1);
}

public sealed record AiClientOptions
{
    /// <summary>
    /// Where events are raised. Null: on SDK threads. Avalonia: <c>action => Dispatcher.UIThread.Post(action)</c>.
    /// </summary>
    public Action<Action>? Dispatch { get; init; }
}

public enum AiTransitionKind
{
    Cut,
    Dissolve,
}

public sealed record AiTransition(AiTransitionKind Kind, uint DurationMs);

/// <summary>Arbiter decision on a suggestion (<see cref="AiDecisionMode.Auto"/> only).</summary>
public sealed record AiVerdict(bool Accepted, string Reason, double AgeMs);

/// <summary>A shot suggested by the worker.</summary>
public sealed record AiSuggestion
{
    public required string DecisionId { get; init; }

    public required string CameraId { get; init; }

    /// <summary>Optional higher-level scene or layout id.</summary>
    public string? SceneId { get; init; }

    public float Confidence { get; init; }

    /// <summary>Why, in words (debug).</summary>
    public string Reason { get; init; } = "";

    public AiTransition Transition { get; init; } = new(AiTransitionKind.Cut, 0);

    /// <summary>Do not cut away before this.</summary>
    public uint MinHoldMs { get; init; }

    /// <summary>Session time of the newest input used by the worker.</summary>
    public long SourcePtsUs { get; init; }

    /// <summary>Session time when the SDK received it.</summary>
    public long ReceivedPtsUs { get; init; }

    /// <summary>End-to-end latency: received - source.</summary>
    public double LatencyMs => (ReceivedPtsUs - SourcePtsUs) / 1000.0;

    /// <summary>Worker compute time.</summary>
    public float ProcessingMs { get; init; }

    /// <summary>Per-camera score (debug).</summary>
    public IReadOnlyDictionary<string, float> Scores { get; init; } = new Dictionary<string, float>();

    /// <summary>Null in <see cref="AiDecisionMode.Suggest"/>.</summary>
    public AiVerdict? Verdict { get; init; }
}

public enum AiOnAirSource
{
    /// <summary>First shot of the session.</summary>
    Initial,

    /// <summary>Suggestion accepted by the arbiter (<see cref="AiDecisionMode.Auto"/>).</summary>
    Ai,

    /// <summary>Reported by the app (<see cref="AiClient.ReportOnAir"/>).</summary>
    Manual,
}

public sealed record AiOnAirChange(string CameraId, AiOnAirSource Source, long PtsUs);

public sealed record AiStateChange(AiConnectionState State, string Detail);

/// <param name="Source">"worker" (session messages), the provider name ("local"), "audio" or "client".</param>
public sealed record AiLogEntry(string Source, string Message);
