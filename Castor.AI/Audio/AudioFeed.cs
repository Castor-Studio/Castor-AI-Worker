using System.Diagnostics;

namespace Castor.AI.Audio;

internal delegate void AudioBlockSink(ReadOnlySpan<float> interleaved);

/// <summary>
/// Turns per-microphone sample pushes (any rate, any block size, any thread) into the steady
/// stream the workers expect: interleaved 16 kHz float32 blocks of 512 frames (32 ms), paced on
/// a real-time clock. A mic with nothing buffered contributes silence; a mic that pushes too much
/// keeps only its newest second, so latency stays bounded.
/// </summary>
internal sealed class AudioFeed : IDisposable
{
    public const int SampleRate = 16_000;
    public const int BlockFrames = 512;
    private const int MaxBufferedFrames = SampleRate;

    private readonly ChannelBuffer[] _channels;
    private readonly AudioBlockSink _sink;
    private Thread? _thread;
    private volatile bool _running;

    public AudioFeed(int channels, AudioBlockSink sink)
    {
        _channels = Enumerable.Range(0, channels).Select(_ => new ChannelBuffer()).ToArray();
        _sink = sink;
    }

    public int Channels => _channels.Length;

    public void Write(int channel, ReadOnlySpan<float> samples, int sampleRate)
    {
        if ((uint)channel < (uint)_channels.Length && sampleRate > 0)
        {
            _channels[channel].Write(samples, sampleRate);
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "castor-ai-audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }

    /// <summary>Builds one interleaved block from what each channel has buffered.</summary>
    internal void FillBlock(float[] block, float[] mono)
    {
        var channels = _channels.Length;
        for (var channel = 0; channel < channels; channel++)
        {
            _channels[channel].Read(mono);
            for (var i = 0; i < BlockFrames; i++)
            {
                block[i * channels + channel] = mono[i];
            }
        }
    }

    private void Run()
    {
        var mono = new float[BlockFrames];
        var block = new float[BlockFrames * _channels.Length];
        var clock = Stopwatch.StartNew();
        long framesWritten = 0;

        while (_running)
        {
            var due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            while (framesWritten + BlockFrames <= due)
            {
                FillBlock(block, mono);
                _sink(block);
                framesWritten += BlockFrames;
            }

            Thread.Sleep(5);
        }
    }

    private sealed class ChannelBuffer
    {
        private readonly Lock _gate = new();
        private readonly Queue<float> _samples = new();
        private LinearResampler? _resampler;

        public void Write(ReadOnlySpan<float> samples, int sampleRate)
        {
            lock (_gate)
            {
                if (_resampler?.InputRate != sampleRate)
                {
                    _resampler = new LinearResampler(sampleRate, SampleRate);
                }

                _resampler.Process(samples, _samples);
                while (_samples.Count > MaxBufferedFrames)
                {
                    _samples.Dequeue();
                }
            }
        }

        public void Read(Span<float> destination)
        {
            lock (_gate)
            {
                var available = Math.Min(destination.Length, _samples.Count);
                for (var i = 0; i < available; i++)
                {
                    destination[i] = _samples.Dequeue();
                }

                destination[available..].Clear();
            }
        }
    }
}

/// <summary>Streaming linear interpolation resampler (mono). Good enough for voice activity detection.</summary>
internal sealed class LinearResampler(int inputRate, int outputRate)
{
    private readonly double _step = (double)inputRate / outputRate;
    private double _position;
    private float _previous;
    private bool _hasPrevious;

    public int InputRate { get; } = inputRate;

    public void Process(ReadOnlySpan<float> input, Queue<float> output)
    {
        if (InputRate == outputRate)
        {
            foreach (var sample in input)
            {
                output.Enqueue(sample);
            }

            return;
        }

        foreach (var sample in input)
        {
            if (!_hasPrevious)
            {
                _previous = sample;
                _hasPrevious = true;
                continue;
            }

            // Output samples falling between the previous input sample (0) and this one (1).
            while (_position < 1.0)
            {
                output.Enqueue(_previous + (float)((sample - _previous) * _position));
                _position += _step;
            }

            _position -= 1.0;
            _previous = sample;
        }
    }
}
