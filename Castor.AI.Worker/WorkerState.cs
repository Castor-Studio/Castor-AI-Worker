using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker;

/// <summary>
/// Process-wide state exposed through <c>Health</c>.
/// </summary>
public sealed class WorkerState
{
    private int _status = (int)HealthResponse.Types.Status.Starting;
    private int _activeSessions;
    private string _detail = "";

    public string WorkerId { get; } = $"local-{Environment.ProcessId}";

    public HealthResponse.Types.Status Status
    {
        get => (HealthResponse.Types.Status)Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, (int)value);
    }

    /// <summary>What the worker is doing while STARTING (e.g. model download progress). Empty when ready.</summary>
    public string Detail
    {
        get => Volatile.Read(ref _detail);
        set => Volatile.Write(ref _detail, value);
    }

    public int ActiveSessions => Volatile.Read(ref _activeSessions);

    public void SessionStarted() => Interlocked.Increment(ref _activeSessions);

    public void SessionEnded() => Interlocked.Decrement(ref _activeSessions);
}
