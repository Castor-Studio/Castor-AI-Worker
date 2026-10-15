using Castor.AI.Contracts.Media;

namespace Castor.AI.Worker.Tests.Audio;

public class AudioRingTests
{
    [Fact]
    public void Reader_sees_header_and_frames_across_wrap()
    {
        var name = AudioRingLayout.RegionName(Guid.NewGuid().ToString("N"));
        using var writer = AudioRingWriter.Create(name, channels: 3, sampleRate: 16_000, capacityFrames: 10, basePtsUs: 1234);
        using var reader = AudioRingReader.Open(name);

        Assert.Equal(3, reader.Channels);
        Assert.Equal(16_000, reader.SampleRate);
        Assert.Equal(10, reader.CapacityFrames);
        Assert.Equal(1234, reader.BasePtsUs);

        writer.Write(Frames(0, 8));
        writer.Write(Frames(8, 6)); // wraps at frame 10
        Assert.Equal(14, reader.WriteFrame);

        var buffer = new float[6 * 3];
        Assert.True(reader.TryRead(8, 6, buffer));
        Assert.Equal(Frames(8, 6), buffer);
    }

    [Fact]
    public void Overwritten_range_is_rejected()
    {
        var name = AudioRingLayout.RegionName(Guid.NewGuid().ToString("N"));
        using var writer = AudioRingWriter.Create(name, channels: 1, sampleRate: 16_000, capacityFrames: 8, basePtsUs: 0);
        using var reader = AudioRingReader.Open(name);

        writer.Write(Frames(0, 8, channels: 1));
        writer.Write(Frames(8, 4, channels: 1));

        var buffer = new float[4];
        Assert.False(reader.TryRead(0, 4, buffer)); // frames 0..3 were overwritten by 8..11
        Assert.True(reader.TryRead(4, 4, buffer));
        Assert.False(reader.TryRead(10, 4, buffer)); // not written yet
    }

    /// <summary>Frame f, channel c holds f * 10 + c.</summary>
    private static float[] Frames(int first, int count, int channels = 3)
    {
        var samples = new float[count * channels];
        for (var f = 0; f < count; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                samples[f * channels + c] = (first + f) * 10 + c;
            }
        }

        return samples;
    }
}
