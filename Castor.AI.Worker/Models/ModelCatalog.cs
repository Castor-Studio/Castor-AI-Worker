using System.Text.Json;

namespace Castor.AI.Worker.Models;

/// <summary>
/// A model file the worker needs, pinned by SHA-256. <see cref="Url"/> is where to get it when it is not
/// installed; null = only through <c>CASTOR_MODELS_URL</c>. File names are unique across domains: hosted
/// models sit side by side in one release.
/// </summary>
public sealed record ModelSpec(string Domain, string FileName, string Sha256, string? Url)
{
    public string Key => $"{Domain}/{FileName}";
}

/// <summary>
/// Every model of every domain, read from <c>models.json</c> (embedded resource): where they are hosted and
/// their SHA-256. Changing a link or a model = editing that file, not this code.
/// <c>CASTOR_MODELS_MANIFEST</c> points to another <c>models.json</c>, for tests or a mirror without a rebuild.
/// </summary>
public static class ModelCatalog
{
    public const string ManifestVariable = "CASTOR_MODELS_MANIFEST";

    private const string ResourceName = "Castor.AI.Worker.Models.models.json";

    private static readonly Lazy<ModelManifest> Manifest = new(Load);

    /// <summary>GitHub release holding the models, e.g. <c>https://github.com/owner/repo/releases/download/models-v1</c>.</summary>
    public static string ReleaseUrl => Manifest.Value.ReleaseUrl;

    public static IReadOnlyList<ModelSpec> All => Manifest.Value.Specs;

    /// <summary>Silero VAD v6.2.1, podcast domain.</summary>
    public static ModelSpec SileroVad => Get("podcast", "silero_vad.onnx");

    /// <summary>Ball detector (Castor-AI checkpoint_5.pt as ONNX), football domain.</summary>
    public static ModelSpec FootballBall => Get("football", "checkpoint_5_416.onnx");

    public static ModelSpec Get(string domain, string fileName) =>
        All.FirstOrDefault(s => s.Domain == domain && s.FileName == fileName)
        ?? throw new InvalidDataException($"Model {domain}/{fileName} missing from models.json");

    /// <summary>Parses a manifest. Public for tests and tools.</summary>
    public static ModelManifest Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("Empty models.json");
        if (string.IsNullOrWhiteSpace(file.Repository) || string.IsNullOrWhiteSpace(file.ReleaseTag))
        {
            throw new InvalidDataException("models.json needs repository and release_tag");
        }

        var releaseUrl = $"https://github.com/{file.Repository}/releases/download/{file.ReleaseTag}";
        var specs = (file.Models ?? []).Select(m =>
        {
            if (string.IsNullOrWhiteSpace(m.Domain) || string.IsNullOrWhiteSpace(m.File) || m.Sha256 is not { Length: 64 })
            {
                throw new InvalidDataException($"models.json: entry '{m.Domain}/{m.File}' needs domain, file and a SHA-256");
            }

            return new ModelSpec(m.Domain, m.File, m.Sha256.ToLowerInvariant(), m.Url ?? $"{releaseUrl}/{m.File}");
        }).ToList();

        var duplicate = specs.GroupBy(s => s.FileName).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"models.json: file name '{duplicate.Key}' used twice (one release holds them all)");
        }

        return new ModelManifest(releaseUrl, specs);
    }

    private static ModelManifest Load()
    {
        if (Environment.GetEnvironmentVariable(ManifestVariable) is { Length: > 0 } path)
        {
            return Parse(File.ReadAllText(path));
        }

        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Resource {ResourceName} not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record ManifestFile(string Repository, string ReleaseTag, List<ManifestModel> Models);

    /// <param name="Url">Optional: replaces the release asset URL for this model.</param>
    /// <param name="Upstream">Where scripts/publish-models.ps1 fetches a model this repository does not produce.</param>
    private sealed record ManifestModel(string Domain, string File, string Sha256, string? Url, string? Upstream);
}

public sealed record ModelManifest(string ReleaseUrl, IReadOnlyList<ModelSpec> Specs);
