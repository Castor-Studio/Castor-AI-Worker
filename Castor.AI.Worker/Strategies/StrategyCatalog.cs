using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Models;
using Castor.AI.Worker.Strategies.Football;
using Castor.AI.Worker.Strategies.Podcast;

namespace Castor.AI.Worker.Strategies;

/// <summary>
/// Maps the domain chosen at launch to its strategy, capabilities and models.
/// One worker process serves exactly one domain.
/// </summary>
public sealed class StrategyCatalog(WorkerOptions options, WorkerState state, ModelStore models)
{
    public static IReadOnlyList<string> KnownDomains { get; } = [PodcastStrategy.Domain, FootballStrategy.Domain];

    public static bool IsKnown(string domain) => KnownDomains.Contains(domain);

    public WorkerCapabilities Capabilities { get; } = options.Domain switch
    {
        PodcastStrategy.Domain => PodcastStrategy.Describe(state.WorkerId),
        FootballStrategy.Domain => FootballStrategy.Describe(state.WorkerId),
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.Domain, "Unknown domain"),
    };

    public bool Supports(string domain) => domain == options.Domain;

    /// <summary>Models of the domain, downloaded by <see cref="Warmup"/> when not installed.</summary>
    public IReadOnlyList<ModelSpec> RequiredModels { get; } = options.Domain switch
    {
        PodcastStrategy.Domain => [ModelCatalog.SileroVad],
        FootballStrategy.Domain => [ModelCatalog.FootballBall],
        _ => [],
    };

    /// <summary>Loads the domain models (local paths from <see cref="RequiredModels"/>). Called once by <see cref="Warmup"/>.</summary>
    public void LoadModels(IReadOnlyDictionary<ModelSpec, string> paths)
    {
        switch (options.Domain)
        {
            case PodcastStrategy.Domain:
                models.LoadVad(paths[ModelCatalog.SileroVad]);
                break;
            case FootballStrategy.Domain:
                models.LoadBall(paths[ModelCatalog.FootballBall]);
                break;
        }
    }

    public IDomainStrategy Create() => options.Domain switch
    {
        PodcastStrategy.Domain => new PodcastStrategy(models.Vad),
        FootballStrategy.Domain => new FootballStrategy(models.Ball),
        _ => throw new InvalidOperationException($"Unknown domain '{options.Domain}'"),
    };
}
