using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker.Strategies;

/// <summary>
/// Domain logic of a worker. Called under a session lock, never concurrently.
/// </summary>
public interface IDomainStrategy : IDisposable
{
    /// <param name="log">Sends an info line to the app (WorkerLog).</param>
    void Start(SessionStart start, SessionMedia media, Action<string> log);

    void OnPayload(PayloadEvent payload);

    /// <summary>Feedback from the app: what is really on air (AI or manual).</summary>
    void OnAir(CameraOnAir onAir);

    /// <summary>Consumes pending media, returns a suggestion or null when there is nothing new to say.</summary>
    SceneDecision? Evaluate(long nowUs);

    /// <summary>Adds domain diagnostics to the periodic stats.</summary>
    void FillStats(WorkerStats stats);
}
