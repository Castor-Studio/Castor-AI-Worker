using System.Security.Cryptography;

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
/// Every model of every domain, hosted as assets of the <see cref="ReleaseTag"/> release of this repository
/// (scripts/publish-models.ps1). Assets of a release never change: a new model = new file name and SHA-256,
/// published in a new release tag.
/// </summary>
public static class ModelCatalog
{
    public const string ReleaseTag = "models-v1";

    public const string ReleaseUrl = $"https://github.com/Castor-Studio/Castor.AI.Worker/releases/download/{ReleaseTag}";

    /// <summary>Silero VAD v6.2.1 (MIT), same version as Castor-AI (silero-vad==6.2.1).</summary>
    public static readonly ModelSpec SileroVad = new(
        "podcast",
        "silero_vad.onnx",
        "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3",
        $"{ReleaseUrl}/silero_vad.onnx");

    /// <summary>
    /// Castor-AI <c>checkpoint_5.pt</c> exported by scripts/export-football-model.ps1. The export is not
    /// byte-reproducible: publish exactly the file this hash was taken from.
    /// </summary>
    public static readonly ModelSpec FootballBall = new(
        "football",
        "checkpoint_5_416.onnx",
        "c25e2369251fc35633326c7b17c5bd0e591f1e261dddd0912d5ecead9614e2dc",
        $"{ReleaseUrl}/checkpoint_5_416.onnx");

    public static IReadOnlyList<ModelSpec> All { get; } = [SileroVad, FootballBall];
}

/// Makes a model available locally: an installed pack (<see cref="PackLocator"/>), else the download cache,
/// else downloads it into the cache. Downloads are checked against <see cref="ModelSpec.Sha256"/> before use
/// and land atomically, so a broken or partial file is never loaded.
/// </summary>
public sealed class ModelProvisioner(HttpClient http, string cacheDirectory, string? baseUrl, Func<ModelSpec, string?> findInstalled)
{
    public const string BaseUrlVariable = "CASTOR_MODELS_URL";
    public const string CacheVariable = "CASTOR_MODELS_DIR";

    private const int BufferSize = 1 << 16;

    /// <summary>
    /// Cache: <c>CASTOR_MODELS_DIR</c>, else <c>%LOCALAPPDATA%\Castor\models</c>.
    /// Source: <c>CASTOR_MODELS_URL</c> (<c>{url}/{file}</c>, e.g. a mirror or another release), else <see cref="ModelSpec.Url"/>.
    /// </summary>
    public static ModelProvisioner Default(HttpClient http) => new(
        http,
        Environment.GetEnvironmentVariable(CacheVariable) is { Length: > 0 } cache
            ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castor", "models"),
        Environment.GetEnvironmentVariable(BaseUrlVariable),
        spec => PackLocator.Find(spec.Domain, spec.FileName));

    public string CachePath(ModelSpec spec) => Path.Combine(cacheDirectory, spec.Domain, spec.FileName);

    /// <param name="progress">Human readable status ("downloading football/... 42 %"), for Health and logs.</param>
    public async Task<string> EnsureAsync(ModelSpec spec, Action<string> progress, CancellationToken ct)
    {
        if (findInstalled(spec) is { } installed)
        {
            return installed;
        }

        var cached = CachePath(spec);
        if (File.Exists(cached))
        {
            return cached;
        }

        var url = SourceOf(spec)
                  ?? throw new FileNotFoundException(
                      $"Model {spec.Key} is not installed and has no download source yet. " +
                      $"Set {BaseUrlVariable}, or install it in packs/{spec.Domain}/models/ (scripts/export-football-model.ps1 for football).");

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        var temporary = $"{cached}.{Environment.ProcessId}.download";
        try
        {
            await DownloadAsync(spec, url, temporary, progress, ct);
            try
            {
                File.Move(temporary, cached);
            }
            catch (IOException) when (File.Exists(cached))
            {
                // Another worker finished the same download first: its file passed the same check.
            }
        }
        finally
        {
            File.Delete(temporary);
        }

        progress($"{spec.Key} ready");
        return cached;
    }

    private string? SourceOf(ModelSpec spec) =>
        !string.IsNullOrEmpty(baseUrl) ? $"{baseUrl.TrimEnd('/')}/{spec.FileName}" : spec.Url;

    private async Task DownloadAsync(ModelSpec spec, string url, string path, Action<string> progress, CancellationToken ct)
    {
        progress($"downloading {spec.Key} from {url}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download of {spec.Key} failed: HTTP {(int)response.StatusCode} from {url}");
        }

        var total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            long received = 0;
            var reported = -1L;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var step = total > 0 ? received * 100 / total.Value : received >> 23; // percent, or every 8 MB
                if (step != reported)
                {
                    reported = step;
                    progress(total > 0
                        ? FormattableString.Invariant($"downloading {spec.Key} {step} % ({received / 1e6:0.0} / {total.Value / 1e6:0.0} MB)")
                        : FormattableString.Invariant($"downloading {spec.Key} {received / 1e6:0.0} MB"));
                }
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, spec.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Checksum mismatch for {spec.Key} from {url}: expected {spec.Sha256}, got {actual}");
        }
    }
}
