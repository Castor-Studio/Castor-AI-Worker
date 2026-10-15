using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Castor.AI.Worker.Audio;

/// <summary>Speech probability source for one audio stream (stateful).</summary>
public interface ISpeechProbability
{
    /// <summary>chunk: exactly <see cref="SileroVadModel.ChunkSamples"/> mono samples in [-1, 1].</summary>
    float Process(ReadOnlySpan<float> chunk);

    void Reset();
}

/// <summary>
/// Silero VAD ONNX model (v6, 16 kHz), shared by all streams.
/// Port of <c>silero_vad.utils_vad.OnnxWrapper</c>: the recurrent state and the
/// 64-sample context live in <see cref="SileroVadStream"/>, one per stream.
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is thread-safe.
/// </summary>
public sealed class SileroVadModel : IDisposable
{
    public const int SampleRate = 16_000;
    public const int ChunkSamples = 512;
    public const int ContextSamples = 64;
    internal const int StateLength = 2 * 1 * 128;

    private static readonly int[] InputShape = [1, ContextSamples + ChunkSamples];
    private static readonly int[] StateShape = [2, 1, 128];

    private readonly InferenceSession _session;
    private readonly DenseTensor<long> _sampleRate = new(new long[] { SampleRate }, ReadOnlySpan<int>.Empty);

    public SileroVadModel(string modelPath)
    {
        using var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
        ModelPath = modelPath;
    }

    public string ModelPath { get; }

    public SileroVadStream CreateStream() => new(this);

    /// <param name="input">context (64) + chunk (512).</param>
    /// <param name="state">recurrent state (2x1x128), updated in place.</param>
    internal float Run(float[] input, float[] state)
    {
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, InputShape)),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, StateShape)),
            NamedOnnxValue.CreateFromTensor("sr", _sampleRate),
        };

        using var results = _session.Run(inputs);
        float probability = 0;
        foreach (var result in results)
        {
            switch (result.Name)
            {
                case "output":
                    probability = result.AsTensor<float>().GetValue(0);
                    break;
                case "stateN":
                    var next = result.AsTensor<float>();
                    for (var i = 0; i < StateLength; i++)
                    {
                        state[i] = next.GetValue(i);
                    }
                    break;
            }
        }

        return probability;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Per-stream Silero state. Never share one instance between streams.</summary>
public sealed class SileroVadStream(SileroVadModel model) : ISpeechProbability
{
    private readonly float[] _input = new float[SileroVadModel.ContextSamples + SileroVadModel.ChunkSamples];
    private readonly float[] _state = new float[SileroVadModel.StateLength];

    public float Process(ReadOnlySpan<float> chunk)
    {
        if (chunk.Length != SileroVadModel.ChunkSamples)
        {
            throw new ArgumentException($"Expected {SileroVadModel.ChunkSamples} samples, got {chunk.Length}", nameof(chunk));
        }

        chunk.CopyTo(_input.AsSpan(SileroVadModel.ContextSamples));
        var probability = model.Run(_input, _state);

        // Next context = last 64 samples of [context, chunk].
        _input.AsSpan(SileroVadModel.ChunkSamples, SileroVadModel.ContextSamples).CopyTo(_input);
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_state);
    }
}
