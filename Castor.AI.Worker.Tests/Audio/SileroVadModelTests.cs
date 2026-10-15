using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Castor.AI.Worker.Audio;
using Castor.AI.Worker.Models;
using Castor.AI.Worker.Tests.Models;

namespace Castor.AI.Worker.Tests.Audio;

/// <summary>
/// Runs the real Silero model (downloaded on first run, like the worker does).
/// </summary>
[SupportedOSPlatform("windows")] // System.Speech TTS
public sealed class SileroVadModelTests : IDisposable
{
    private readonly SileroVadModel _model = new(TestModels.Path(ModelCatalog.SileroVad));

    [Fact]
    public void Digital_silence_is_not_speech()
    {
        var tracker = NewTracker();
        var silence = new float[SileroVadModel.ChunkSamples];

        for (var i = 0; i < 10; i++)
        {
            tracker.Process(silence);
        }

        Assert.False(tracker.IsSpeaking);
        Assert.True(tracker.LastProbability < 0.1f, $"probability {tracker.LastProbability}");
    }

    [Fact]
    public void Synthesized_speech_is_detected_then_released()
    {
        var samples = Synthesize("Bonjour et bienvenue dans ce podcast. Aujourd'hui nous parlons d'intelligence artificielle.");
        var tracker = NewTracker();
        var speakingChunks = 0;

        foreach (var chunk in Chunks(samples))
        {
            if (tracker.Process(chunk))
            {
                speakingChunks++;
            }
        }

        // Most of the sentence is speech.
        Assert.True(speakingChunks > Chunks(samples).Count() / 2, $"{speakingChunks} speaking chunks");

        // One second of silence afterwards ends the segment (min_silence 400 ms).
        foreach (var chunk in Chunks(new float[SileroVadModel.SampleRate]))
        {
            tracker.Process(chunk);
        }

        Assert.False(tracker.IsSpeaking);
    }

    [Fact]
    public void Streams_keep_independent_state()
    {
        var speech = Synthesize("Un deux trois quatre cinq six sept huit neuf dix.");
        var talker = NewTracker();
        var quiet = NewTracker();
        var silence = new float[SileroVadModel.ChunkSamples];

        foreach (var chunk in Chunks(speech))
        {
            talker.Process(chunk);
            quiet.Process(silence);
        }

        Assert.False(quiet.IsSpeaking);
        Assert.True(quiet.LastProbability < 0.1f);
    }

    public void Dispose() => _model.Dispose();

    private SpeechActivityTracker NewTracker() =>
        new(new VadIterator(_model.CreateStream(), threshold: 0.5f, minSilenceDurationMs: 400, speechPadMs: 100));

    private static IEnumerable<float[]> Chunks(float[] samples)
    {
        for (var offset = 0; offset + SileroVadModel.ChunkSamples <= samples.Length; offset += SileroVadModel.ChunkSamples)
        {
            yield return samples[offset..(offset + SileroVadModel.ChunkSamples)];
        }
    }

    /// <summary>Windows TTS, 16 kHz mono PCM16, converted to float32.</summary>
    private static float[] Synthesize(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        using var wav = new MemoryStream();
        synthesizer.SetOutputToAudioStream(
            wav,
            new SpeechAudioFormatInfo(SileroVadModel.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Speak(text);

        var bytes = wav.ToArray();
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }

        return samples;
    }
}
