using System.Diagnostics;

namespace Castor.AI.Worker;

/// <summary>
/// Shuts the worker down when the app that spawned it goes away,
/// so a crashed app never leaves an orphan worker holding the GPU.
/// </summary>
public sealed class ParentWatchdog(
    WorkerOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ParentWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ParentPid is not int pid)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Parent process {Pid} not found, shutting down", pid);
            lifetime.StopApplication();
            return;
        }

        using (parent)
        {
            await parent.WaitForExitAsync(stoppingToken);
        }

        logger.LogWarning("Parent process {Pid} exited, shutting down", pid);
        lifetime.StopApplication();
    }
}
