using Castor.AI;
using Castor.AI.Providers;
using Castor.AI.Providers.Local;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public sealed class CastorAIOptions
{
    public LocalWorkerOptions Worker { get; set; } = new();

    public AiClientOptions Client { get; set; } = new();
}

public static class CastorAIServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AiClient"/> (singleton) backed by a local worker process.
    /// <code>
    /// services.AddCastorAI(o => o.Client = new AiClientOptions { Dispatch = a => Dispatcher.UIThread.Post(a) });
    /// </code>
    /// </summary>
    public static IServiceCollection AddCastorAI(this IServiceCollection services, Action<CastorAIOptions>? configure = null)
    {
        var options = new CastorAIOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options.Worker);
        services.TryAddSingleton<IAiProvider>(provider => new LocalWorkerProvider(provider.GetRequiredService<LocalWorkerOptions>()));
        services.TryAddSingleton(provider => new AiClient(provider.GetRequiredService<IAiProvider>(), options.Client));
        return services;
    }
}
