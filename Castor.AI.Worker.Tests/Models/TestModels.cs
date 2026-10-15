using Castor.AI.Worker.Models;

namespace Castor.AI.Worker.Tests.Models;

/// <summary>
/// Real models for the tests, found or downloaded exactly like the worker does (packs/, cache, release).
/// </summary>
internal static class TestModels
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Path(ModelSpec spec) =>
        ModelProvisioner.Default(Http).EnsureAsync(spec, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
}
