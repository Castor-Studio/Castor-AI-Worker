using Castor.AI.Worker.Audio;
using Castor.AI.Worker.Models;
using Microsoft.Extensions.Logging;

namespace Castor.AI.Worker.Tests.Models;

public sealed class InferenceDeviceTests
{
    [Fact]
    public void Default_device_is_gpu() => Assert.Equal("gpu", WorkerOptions.Parse([]).Device);

    [Theory]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("CPU")]
    public void Parses_explicit_device(string device) =>
        Assert.Equal(device.ToLowerInvariant(), WorkerOptions.Parse(["--device", device]).Device);

    [Theory]
    [InlineData("cuda")]
    [InlineData("")]
    public void Rejects_invalid_device(string device) =>
        Assert.Throws<ArgumentException>(() => WorkerOptions.Parse(["--device", device]));

    [Fact]
    public void Rejects_missing_device() =>
        Assert.Throws<ArgumentException>(() => WorkerOptions.Parse(["--device"]));

    [Fact]
    public void Parses_gpu_adapter() =>
        Assert.Equal(1, WorkerOptions.Parse(["--gpu-adapter", "1"]).GpuAdapter);

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void Rejects_invalid_gpu_adapter(string value) =>
        Assert.Throws<ArgumentException>(() => WorkerOptions.Parse(["--gpu-adapter", value]));

    [Fact]
    public void Unavailable_adapter_falls_back_to_cpu_with_warning()
    {
        var logger = new RecordingLogger();
        using var model = new SileroVadModel(TestModels.Path(ModelCatalog.SileroVad),
            new OnnxSessionFactory("gpu", logger, gpuAdapter: int.MaxValue));
        Assert.Equal("CPUExecutionProvider", model.ExecutionProvider);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("falling back to CPU"));
        Assert.InRange(model.CreateStream().Process(new float[SileroVadModel.ChunkSamples]), 0, 0.1f);
    }

    [Fact]
    public void Failed_gpu_warmup_disposes_session_and_retries_on_cpu()
    {
        var logger = new RecordingLogger();
        var attempts = 0;
        var (session, provider) = new OnnxSessionFactory("gpu", logger).Create(TestModels.Path(ModelCatalog.SileroVad), _ =>
        {
            if (++attempts == 1 && logger.Entries.All(e => e.Level != LogLevel.Warning))
                throw new DllNotFoundException("simulated first inference provider failure");
        });
        using (session)
        {
            Assert.Equal("CPUExecutionProvider", provider);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        }
    }

    private sealed class RecordingLogger : ILogger<OnnxSessionFactory>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
