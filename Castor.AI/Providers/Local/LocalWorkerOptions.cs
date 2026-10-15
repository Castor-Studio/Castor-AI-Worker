namespace Castor.AI.Providers.Local;

public sealed record LocalWorkerOptions
{
    /// <summary>
    /// Worker executable. Null: <c>CASTOR_WORKER_PATH</c>, then <c>Castor.AI.Worker.exe</c> next to the app,
    /// then the sibling project output (dev layout).
    /// </summary>
    public string? WorkerPath { get; init; }

    /// <summary>
    /// Max time for the worker to report READY, counted from its last progress (model download, loading):
    /// a long download does not time out while it advances.
    /// </summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Delays before reopening a crashed session; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RestartBackoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>Length of the shared memory audio ring.</summary>
    public int AudioRingSeconds { get; init; } = 2;
}
