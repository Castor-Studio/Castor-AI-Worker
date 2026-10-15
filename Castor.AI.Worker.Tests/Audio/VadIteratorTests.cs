using Castor.AI.Worker.Audio;

namespace Castor.AI.Worker.Tests.Audio;

public class VadIteratorTests
{
    private static readonly float[] Chunk = new float[SileroVadModel.ChunkSamples];

    [Fact]
    public void Starts_on_first_chunk_above_threshold()
    {
        var events = Run([0.1f, 0.2f, 0.6f, 0.9f]);

        Assert.Equal([VadEvent.None, VadEvent.None, VadEvent.SpeechStart, VadEvent.None], events);
    }

    [Fact]
    public void Ends_after_min_silence_duration()
    {
        // min_silence 100 ms = 1600 samples: end reported 4 chunks (2048 samples) after the first quiet one.
        var events = Run([0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f], minSilenceMs: 100);

        Assert.Equal(
            [VadEvent.SpeechStart, VadEvent.None, VadEvent.None, VadEvent.None, VadEvent.None, VadEvent.SpeechEnd],
            events);
    }

    [Fact]
    public void Hysteresis_band_keeps_speech_going()
    {
        // Between threshold - 0.15 and threshold: neither start nor end.
        var events = Run([0.9f, 0.4f, 0.4f, 0.4f, 0.4f, 0.4f, 0.4f], minSilenceMs: 100);

        Assert.DoesNotContain(VadEvent.SpeechEnd, events);
    }

    [Fact]
    public void Speech_resuming_cancels_pending_end()
    {
        var events = Run([0.9f, 0.1f, 0.1f, 0.9f, 0.1f, 0.1f, 0.1f], minSilenceMs: 100);

        Assert.DoesNotContain(VadEvent.SpeechEnd, events);
    }

    [Fact]
    public void Tracker_follows_events()
    {
        var tracker = new SpeechActivityTracker(new VadIterator(new Scripted([0.9f, 0.1f, 0.1f, 0.1f, 0.1f]), minSilenceDurationMs: 50));

        Assert.True(tracker.Process(Chunk));
        Assert.True(tracker.Process(Chunk));
        Assert.True(tracker.Process(Chunk));
        Assert.False(tracker.Process(Chunk));
    }

    [Fact]
    public void Level_meter_matches_python_rms_to_db()
    {
        Assert.Equal(-100.0, SpeechActivityTracker.RmsToDb(0.0));
        Assert.Equal(0.0, SpeechActivityTracker.RmsToDb(1.0), 6);
        Assert.Equal(1.0, SpeechActivityTracker.Rms([1f, -1f, 1f, -1f]), 6);
    }

    [Fact]
    public void Smoothed_level_survives_a_short_pause()
    {
        var loud = Enumerable.Repeat(0.5f, SileroVadModel.ChunkSamples).ToArray();
        var raw = new SpeechActivityTracker(new VadIterator(new Scripted(new float[20])));
        var smoothed = new SpeechActivityTracker(new VadIterator(new Scripted(new float[20])), levelSmoothingMs: 300);

        for (var i = 0; i < 10; i++)
        {
            raw.Process(loud);
            smoothed.Process(loud);
        }

        raw.Process(Chunk); // 32 ms of digital silence
        smoothed.Process(Chunk);

        Assert.Equal(-100.0, raw.LevelDb);
        Assert.InRange(smoothed.LevelDb, -9.0, -6.0); // -6 dB source, ~0.5 dB decay
    }

    private static List<VadEvent> Run(float[] probabilities, int minSilenceMs = 100)
    {
        var iterator = new VadIterator(new Scripted(probabilities), threshold: 0.5f, minSilenceDurationMs: minSilenceMs);
        return probabilities.Select(_ => iterator.Process(Chunk)).ToList();
    }

    private sealed class Scripted(float[] probabilities) : ISpeechProbability
    {
        private int _index;

        public float Process(ReadOnlySpan<float> chunk) => probabilities[_index++];

        public void Reset() => _index = 0;
    }
}
