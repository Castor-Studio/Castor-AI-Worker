using System.Diagnostics;
using System.Text.Json;
using Castor.AI.Worker.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Castor.AI.Worker.Models;

/// <summary>One-click, on-device CPU/GPU smoke test and latency comparison used by the dashboard.</summary>
public static class WorkerDiagnostics
{
    public const string ResultPrefix = "CASTOR_DIAGNOSTICS:";
    private const int Iterations = 30;

    public static async Task RunAsync(Action<string> report, int gpuAdapter = 0, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; o.ColorBehavior = LoggerColorBehavior.Disabled; }));
        var results = new List<DeviceDiagnostic>();
        var lastOutput = Array.Empty<float>();

        foreach (var spec in new[] { ModelCatalog.FootballBall, ModelCatalog.SileroVad })
        {
            cancellationToken.ThrowIfCancellationRequested();
            report($"Vérification du modèle {spec.Key}…");
            var modelPath = await ModelProvisioner.Default(http).EnsureAsync(spec, report, cancellationToken);
            var isVad = spec == ModelCatalog.SileroVad;
            float[]? cpuOutput = null;
            foreach (var device in new[] { "cpu", "gpu" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sessions = new OnnxSessionFactory(device, loggerFactory.CreateLogger<OnnxSessionFactory>(), gpuAdapter);
                float[] firstOutput;
                double[] samples;
                string provider;
                var (session, selectedProvider) = sessions.Create(modelPath,
                    inference => Run(inference, spec.Key, isVad), singleThreaded: isVad);
                provider = selectedProvider;
                using (session)
                {
                    report($"{spec.Key}: session {device} ({provider}), mesure…");
                    Run(session, spec.Key, isVad); // warmup
                    firstOutput = lastOutput;
                    samples = new double[Iterations];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var start = Stopwatch.GetTimestamp();
                        Run(session, spec.Key, isVad);
                        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }
                }

                if (provider == "CPUExecutionProvider") cpuOutput = firstOutput;
                cancellationToken.ThrowIfCancellationRequested();
                var prefix = Path.Combine(Path.GetTempPath(), $"castor-diagnostic-{Guid.NewGuid():N}");
                var (profiledSession, profiledProvider) = sessions.Create(modelPath,
                    inference => Run(inference, spec.Key, isVad), singleThreaded: isVad, profilePrefix: prefix);
                using (profiledSession)
                {
                    Run(profiledSession, spec.Key, isVad);
                    var tracePath = profiledSession.EndProfiling();
                    try
                    {
                        Array.Sort(samples);
                        var difference = provider != "CPUExecutionProvider" && cpuOutput is not null
                            ? MaxDifference(cpuOutput, firstOutput) : (float?)null;
                        results.Add(new DeviceDiagnostic(spec.Key, device, profiledProvider, samples.Average(),
                            samples[samples.Length / 2], samples[(int)Math.Ceiling(samples.Length * .95) - 1],
                            CountKernels(tracePath), difference,
                            provider == "CPUExecutionProvider" ? "Référence CPU" :
                                difference <= (isVad ? 0.005f : 0.01f) ? "Conforme à la référence CPU" : "Écart CPU/GPU"));
                    }
                    finally
                    {
                        File.Delete(tracePath);
                    }
                }
            }
        }

        report(ResultPrefix + JsonSerializer.Serialize(results));

        void Run(InferenceSession inference, string modelKey, bool isVad)
        {
            var inputs = isVad
                ? new[]
                {
                    NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(new[] { 1, SileroVadModel.ContextSamples + SileroVadModel.ChunkSamples })),
                    NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(new[] { 2, 1, 128 })),
                    NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { SileroVadModel.SampleRate }, ReadOnlySpan<int>.Empty)),
                }
                : new[]
                {
                    NamedOnnxValue.CreateFromTensor(inference.InputMetadata.Single().Key,
                        new DenseTensor<float>(inference.InputMetadata.Single().Value.Dimensions)),
                };
            using var output = inference.Run(inputs);
            lastOutput = output.First().AsTensor<float>().ToArray();
            if (lastOutput.Length == 0 || lastOutput.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"{modelKey} returned empty or non-finite model output");
        }

        static Dictionary<string, int> CountKernels(string tracePath)
        {
            using var trace = JsonDocument.Parse(File.ReadAllText(tracePath));
            return trace.RootElement.EnumerateArray()
                .Where(entry => entry.TryGetProperty("args", out var args) && args.TryGetProperty("provider", out _))
                .GroupBy(entry => entry.GetProperty("args").GetProperty("provider").GetString() ?? "Unknown")
                .ToDictionary(group => group.Key, group => group.Count());
        }

        static float MaxDifference(float[] left, float[] right)
        {
            if (left.Length != right.Length) return float.PositiveInfinity;
            var max = 0f;
            for (var i = 0; i < left.Length; i++) max = Math.Max(max, Math.Abs(left[i] - right[i]));
            return max;
        }
    }

    public sealed record DeviceDiagnostic(string Model, string RequestedDevice, string Provider,
        double MeanMs, double P50Ms, double P95Ms, Dictionary<string, int> KernelPlacements,
        float? MaxDifferenceFromCpu, string OutputComparison);
}
