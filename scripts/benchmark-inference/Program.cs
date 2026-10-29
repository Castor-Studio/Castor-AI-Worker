using System.Diagnostics;
using System.Text.Json;
using Castor.AI.Worker.Audio;
using Castor.AI.Worker.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Same session setup as the worker, warmed up before timing. Profiles verify actual node placement.
var iterations = args.Length > 0 ? int.Parse(args[0]) : 50;
if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {Environment.ProcessorCount} logical CPUs; {iterations} iterations");
foreach (var spec in new[] { ModelCatalog.FootballBall, ModelCatalog.SileroVad })
{
    var path = await ModelProvisioner.Default(http).EnsureAsync(spec, Console.WriteLine, CancellationToken.None);
    foreach (var device in new[] { "cpu", "gpu" })
    {
        var silero = spec == ModelCatalog.SileroVad;
        var (session, provider) = new OnnxSessionFactory(device).Create(path, Run, singleThreaded: silero);
        using (session)
        {
            for (var i = 0; i < 5; i++) Run(session);
            var times = new double[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                Run(session);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(times);
            Console.WriteLine($"{spec.Key} requested={device} provider={provider}: mean={times.Average():F3} ms p50={times[iterations / 2]:F3} ms p95={times[Math.Min(iterations - 1, (int)Math.Ceiling(iterations * .95) - 1)]:F3} ms");
        }

        // Profile separately so instrumentation doesn't distort the latency measurement.
        var prefix = Path.Combine(Path.GetTempPath(), $"castor-{Guid.NewGuid():N}");
        var (profiled, _) = new OnnxSessionFactory(device).Create(path, Run, singleThreaded: silero, profilePrefix: prefix);
        using (profiled)
        {
            Run(profiled);
            var profile = profiled.EndProfiling();
            using var json = JsonDocument.Parse(File.ReadAllText(profile));
            var placements = json.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("args", out var a) && a.TryGetProperty("provider", out _))
                .GroupBy(e => e.GetProperty("args").GetProperty("provider").GetString());
            foreach (var group in placements)
                Console.WriteLine($"  profile: {group.Key} = {group.Count()} kernel events");
            File.Delete(profile);
        }

        void Run(InferenceSession inference)
        {
            var inputs = silero
                ? new[]
                {
                    NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(new[] { 1, SileroVadModel.ContextSamples + SileroVadModel.ChunkSamples })),
                    NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(new[] { 2, 1, 128 })),
                    NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { SileroVadModel.SampleRate }, ReadOnlySpan<int>.Empty)),
                }
                : new[] { NamedOnnxValue.CreateFromTensor(inference.InputMetadata.Single().Key,
                    new DenseTensor<float>(inference.InputMetadata.Single().Value.Dimensions)) };
            using var results = inference.Run(inputs);
            // Include host output access, as the worker does.
            foreach (var result in results) _ = result.AsTensor<float>().ToArray();
        }
    }
}
