using Microsoft.ML.OnnxRuntime;
using Microsoft.Extensions.Logging.Abstractions;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Castor.AI.Worker.Models;

/// <summary>Shared provider setup. CPU remains available for unsupported operators and GPU startup failures.</summary>
public sealed class OnnxSessionFactory(string device = "gpu", ILogger<OnnxSessionFactory>? logger = null, int gpuAdapter = 0)
{
    private readonly ILogger _logger = logger ?? NullLogger<OnnxSessionFactory>.Instance;

    public (InferenceSession Session, string Provider) Create(string path, Action<InferenceSession> warmup,
        bool singleThreaded = false, string? profilePrefix = null)
    {
        if (device is not ("cpu" or "gpu"))
            throw new ArgumentException("Device must be 'cpu' or 'gpu'.", nameof(device));

        if (device == "gpu")
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DirectML requires Windows.");

                var session = CreateSession(path, gpu: true, warmup, singleThreaded, profilePrefix);
                _logger.LogInformation("Model {Model}: DirectML GPU (adapter {Adapter}); unsupported operators may use CPU", Path.GetFileName(path), gpuAdapter);
                return (session, "DmlExecutionProvider");
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
                                      or PlatformNotSupportedException or TypeInitializationException)
            {
                _logger.LogWarning("Model {Model}: GPU initialization failed, falling back to CPU: {Reason}", Path.GetFileName(path), ex.Message);
            }
        }

        var cpu = CreateSession(path, gpu: false, warmup, singleThreaded, profilePrefix);
        _logger.LogInformation("Model {Model}: CPU", Path.GetFileName(path));
        return (cpu, "CPUExecutionProvider");
    }

    private InferenceSession CreateSession(string path, bool gpu, Action<InferenceSession> warmup, bool singleThreaded, string? profilePrefix)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = singleThreaded ? 1 : 0,
            IntraOpNumThreads = singleThreaded ? 1 : 0,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = !gpu,
        };
        if (profilePrefix is not null)
        {
            options.EnableProfiling = true;
            options.ProfileOutputPathPrefix = profilePrefix;
        }
        if (gpu)
            options.AppendExecutionProvider_DML(gpuAdapter);

        var session = new InferenceSession(path, options);
        try
        {
            // Some provider errors only surface at the first inference, before READY.
            warmup(session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
