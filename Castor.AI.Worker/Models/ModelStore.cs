using Castor.AI.Worker.Audio;
using Castor.AI.Worker.Strategies.Football;

namespace Castor.AI.Worker.Models;

/// <summary>
/// Models loaded once per process by <see cref="Warmup"/> and shared by all sessions.
/// </summary>
public sealed class ModelStore : IDisposable
{
    private SileroVadModel? _vad;
    private YoloBallModel? _ball;

    public SileroVadModel Vad => _vad ?? throw new InvalidOperationException("Silero VAD model not loaded");

    public YoloBallModel Ball => _ball ?? throw new InvalidOperationException("Ball detection model not loaded");

    public void LoadVad(string path) => _vad = new SileroVadModel(path);

    /// <summary>Loads the YOLO model and runs it once: the first inference is much slower than the next ones.</summary>
    public void LoadBall(string path)
    {
        var model = new YoloBallModel(path);
        model.Run(new float[3 * model.InputSize * model.InputSize], out _);
        _ball = model;
    }

    public void Dispose()
    {
        _vad?.Dispose();
        _ball?.Dispose();
    }
}

/// <summary>
/// Finds the model packs: <c>CASTOR_PACKS_DIR</c>, a <c>packs</c> folder next to the worker,
/// or the closest <c>packs</c> folder above it (dev layout).
/// </summary>
public static class PackLocator
{
    private const string EnvironmentVariable = "CASTOR_PACKS_DIR";

    /// <summary>Installed model path, or null (the worker then downloads it, see <see cref="ModelProvisioner"/>).</summary>
    public static string? Find(string domain, string fileName) =>
        Candidates().Select(packs => Path.Combine(packs, domain, "models", fileName)).FirstOrDefault(File.Exists);

    private static IEnumerable<string> Candidates()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var packs = Path.Combine(directory.FullName, "packs");
            if (Directory.Exists(packs))
            {
                yield return packs;
            }
        }
    }
}
