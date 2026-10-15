using System.Net;
using System.Security.Cryptography;
using Castor.AI.Worker.Models;

namespace Castor.AI.Worker.Tests.Models;

public sealed class ModelProvisionerTests : IDisposable
{
    private static readonly byte[] Content = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"castor-models-{Guid.NewGuid():N}");
    private readonly FakeHandler _handler = new();
    private readonly ModelSpec _spec = new("football", "ball.onnx", Convert.ToHexStringLower(SHA256.HashData(Content)), "https://example.test/ball.onnx");

    [Fact]
    public async Task Installed_pack_wins_and_nothing_is_downloaded()
    {
        var provisioner = Provisioner(installed: @"C:\packs\football\models\ball.onnx");

        Assert.Equal(@"C:\packs\football\models\ball.onnx", await provisioner.EnsureAsync(_spec, _ => { }, default));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Missing_model_is_downloaded_once_into_the_cache_with_progress()
    {
        var provisioner = Provisioner();
        var progress = new List<string>();

        var path = await provisioner.EnsureAsync(_spec, progress.Add, default);
        var again = await provisioner.EnsureAsync(_spec, _ => { }, default);

        Assert.Equal(Path.Combine(_cache, "football", "ball.onnx"), path);
        Assert.Equal(path, again);
        Assert.Equal(Content, await File.ReadAllBytesAsync(path));
        Assert.Equal(["https://example.test/ball.onnx"], _handler.Requests);
        Assert.Contains(progress, p => p.Contains("100 %"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temporary file left
    }

    [Fact]
    public async Task Base_url_overrides_the_catalog_source()
    {
        var provisioner = Provisioner(baseUrl: "https://models.example.test/castor/");

        await provisioner.EnsureAsync(_spec with { Url = null }, _ => { }, default);

        Assert.Equal(["https://models.example.test/castor/ball.onnx"], _handler.Requests);
    }

    [Fact]
    public async Task Corrupted_download_is_rejected_and_not_kept()
    {
        _handler.Body = [.. Content[..^1], 0];
        var provisioner = Provisioner();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => provisioner.EnsureAsync(_spec, _ => { }, default));

        Assert.Contains("Checksum mismatch", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_cache, "football")) && Directory.EnumerateFiles(Path.Combine(_cache, "football")).Any());
    }

    [Fact]
    public async Task Http_error_and_missing_source_are_reported()
    {
        _handler.Status = HttpStatusCode.NotFound;
        await Assert.ThrowsAsync<HttpRequestException>(() => Provisioner().EnsureAsync(_spec, _ => { }, default));

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => Provisioner().EnsureAsync(_spec with { Url = null }, _ => { }, default));
        Assert.Contains(ModelProvisioner.BaseUrlVariable, error.Message);
    }

    [Fact]
    public void Catalog_pins_every_model_in_the_release_with_unique_names()
    {
        foreach (var spec in ModelCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", spec.Sha256);
            Assert.Equal($"{ModelCatalog.ReleaseUrl}/{spec.FileName}", spec.Url);
        }

        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(s => s.FileName).Distinct().Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private ModelProvisioner Provisioner(string? installed = null, string? baseUrl = null) =>
        new(new HttpClient(_handler, disposeHandler: false), _cache, baseUrl, _ => installed);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public byte[] Body { get; set; } = Content;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) });
        }
    }
}
