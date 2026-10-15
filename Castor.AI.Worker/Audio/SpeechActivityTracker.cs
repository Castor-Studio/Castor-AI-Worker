namespace Castor.AI.Worker.Audio;

/// <summary>
/// Per-microphone VAD state plus level meter. One instance per audio channel, never shared.
/// Port of <c>castostudio_ai_podcast.vad.SpeechActivityTracker</c> + the level part of <c>AudioStreamReader</c>.
/// </summary>
/// <param name="levelSmoothingMs">
/// 0 (Python behavior): level of the last 32 ms chunk only. Otherwise exponential smoothing of the
/// energy with this time constant, so a short pause between words does not drop every mic to the
/// same floor (which makes cross-gating keep bleed mics and fakes a debate).
/// </param>
public sealed class SpeechActivityTracker(VadIterator iterator, int levelSmoothingMs = 0)
{
    private readonly double _smoothing = levelSmoothingMs <= 0
        ? 0
        : Math.Exp(-1000.0 * SileroVadModel.ChunkSamples / SileroVadModel.SampleRate / levelSmoothingMs);

    private double _energy;

    public bool IsSpeaking { get; private set; }

    /// <summary>Level of the last chunk (dBFS). Diagnostic and cross-gating input.</summary>
    public double LevelDb { get; private set; } = -100.0;

    public float LastProbability => iterator.LastProbability;

    public bool Process(ReadOnlySpan<float> chunk)
    {
        var rms = Rms(chunk);
        _energy = _smoothing * _energy + (1 - _smoothing) * rms * rms;
        LevelDb = RmsToDb(Math.Sqrt(_energy));
        switch (iterator.Process(chunk))
        {
            case VadEvent.SpeechStart:
                IsSpeaking = true;
                break;
            case VadEvent.SpeechEnd:
                IsSpeaking = false;
                break;
        }

        return IsSpeaking;
    }

    public void Reset()
    {
        iterator.Reset();
        IsSpeaking = false;
        LevelDb = -100.0;
        _energy = 0;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static double RmsToDb(double rms) => rms > 1e-5 ? 20 * Math.Log10(rms) : -100.0;
}
