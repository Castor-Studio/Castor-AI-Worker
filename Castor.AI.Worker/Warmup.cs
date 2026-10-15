using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Models;
using Castor.AI.Worker.Strategies;

namespace Castor.AI.Worker;

/// <summary>
/// Makes the domain models available (installed pack, cache, or download), loads them, then flips Health
/// to READY. Download progress goes to <c>Health.detail</c>. A missing or broken model stops the worker with
/// exit code 3.
/// </summary>
public sealed class Warmup(
    WorkerOptions options,
    WorkerState state,
    StrategyCatalog catalog,
    IHostApplicationLifetime lifetime,
    ILogger<Warmup> logger) : BackgroundService
{
    public const int ModelLoadFailedExitCode = 3;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Loading models for domain '{Domain}'", options.Domain);
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            var provisioner = ModelProvisioner.Default(http);
            var paths = new Dictionary<ModelSpec, string>();
            foreach (var spec in catalog.RequiredModels)
            {
                paths[spec] = await provisioner.EnsureAsync(spec, Report, stoppingToken);
                logger.LogInformation("Model {Model}: {Path}", spec.Key, paths[spec]);
            }

            Report($"loading {string.Join(", ", paths.Keys.Select(s => s.Key))}");
            await Task.Run(() => catalog.LoadModels(paths), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogCritical("Model loading failed: {Message}", ex.Message);
            Environment.ExitCode = ModelLoadFailedExitCode;
            lifetime.StopApplication();
            return;
        }

        state.Detail = "";
        state.Status = HealthResponse.Types.Status.Ready;
        logger.LogInformation("Worker {WorkerId} ready on pipe '{Pipe}'", state.WorkerId, options.PipeName);
    }

    private void Report(string detail)
    {
        // Percent steps only: one log line per change keeps the log readable.
        state.Detail = detail;
        logger.LogInformation("{Detail}", detail);
    }
}
