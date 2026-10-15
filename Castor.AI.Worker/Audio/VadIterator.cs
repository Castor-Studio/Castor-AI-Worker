namespace Castor.AI.Worker.Audio;

public enum VadEvent
{
    None,
    SpeechStart,
    SpeechEnd,
}

/// <summary>
/// Streaming speech segmentation, port of <c>silero_vad.utils_vad.VADIterator</c> (v6.2.1):
/// speech starts at the first chunk above <c>threshold</c>, ends after
/// <c>min_silence_duration_ms</c> below <c>threshold - 0.15</c>.
/// </summary>
public sealed class VadIterator
{
    private readonly ISpeechProbability _model;
    private readonly float _threshold;
    private readonly double _minSilenceSamples;
    private bool _triggered;
    private long _tempEnd;
    private long _currentSample;

    public VadIterator(
        ISpeechProbability model,
        float threshold = 0.5f,
        int sampleRate = SileroVadModel.SampleRate,
        int minSilenceDurationMs = 100,
        int speechPadMs = 30)
    {
        _model = model;
        _threshold = threshold;
        _minSilenceSamples = sampleRate * minSilenceDurationMs / 1000.0;
        // Pad only shifts reported sample positions, which nothing downstream uses.
        _ = speechPadMs;
    }

    public float LastProbability { get; private set; }

    public void Reset()
    {
        _model.Reset();
        _triggered = false;
        _tempEnd = 0;
        _currentSample = 0;
    }

    public VadEvent Process(ReadOnlySpan<float> chunk)
    {
        _currentSample += chunk.Length;
        var probability = _model.Process(chunk);
        LastProbability = probability;

        if (probability >= _threshold && _tempEnd != 0)
        {
            _tempEnd = 0;
        }

        if (probability >= _threshold && !_triggered)
        {
            _triggered = true;
            return VadEvent.SpeechStart;
        }

        if (probability < _threshold - 0.15f && _triggered)
        {
            if (_tempEnd == 0)
            {
                _tempEnd = _currentSample;
            }

            if (_currentSample - _tempEnd < _minSilenceSamples)
            {
                return VadEvent.None;
            }

            _tempEnd = 0;
            _triggered = false;
            return VadEvent.SpeechEnd;
        }

        return VadEvent.None;
    }
}
