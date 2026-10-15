using System.Security.Cryptography;

namespace Castor.AI.Worker.Models;

/// <summary>
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
