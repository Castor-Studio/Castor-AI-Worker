using Castor.AI.Worker.Models;

namespace Castor.AI.Worker.Tests.Models;

/// <summary>
/// Real models for the tests, found or downloaded exactly like the worker does (packs/, cache, release).
/// </summary>
internal static class TestModels
{
    // Run the same parity suite twice: CASTOR_TEST_DEVICE=cpu then gpu.
    public static OnnxSessionFactory Sessions => new(
        Environment.GetEnvironmentVariable("CASTOR_TEST_DEVICE") ?? "gpu",
        gpuAdapter: int.TryParse(Environment.GetEnvironmentVariable("CASTOR_TEST_GPU_ADAPTER"), out var adapter) ? adapter : 0);

    public static void CheckProvider(string provider)
    {
        if (Environment.GetEnvironmentVariable("CASTOR_TEST_DEVICE") == "cpu")
            Assert.Equal("CPUExecutionProvider", provider);
        if (Environment.GetEnvironmentVariable("CASTOR_REQUIRE_GPU") == "1")
            Assert.Equal("DmlExecutionProvider", provider);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Lock DownloadGate = new();

    public static string Path(ModelSpec spec)
    {
        // Parallel test classes can need the same model on an empty cache.
        lock (DownloadGate)
            return ModelProvisioner.Default(Http).EnsureAsync(spec, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
