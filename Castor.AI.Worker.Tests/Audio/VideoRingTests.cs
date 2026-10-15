using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker.Tests.Audio;

public class VideoRingTests
{
    [Fact]
    public void Reader_sees_header_and_latest_frame()
    {
        var name = VideoRingLayout.RegionName(Guid.NewGuid().ToString("N"), 0);
        using var writer = VideoRingWriter.Create(name, width: 4, height: 2, PixelFormat.Bgra8);
        using var reader = VideoRingReader.Open(name);

        Assert.Equal((4, 2, 16, PixelFormat.Bgra8, 3), (reader.Width, reader.Height, reader.Stride, reader.PixelFormat, reader.SlotCount));
        Assert.Equal(0ul, reader.LatestSeq);
        var buffer = new byte[reader.FrameSize];
        Assert.False(reader.TryReadLatest(0, buffer, out _)); // nothing written yet

        Assert.Equal(1ul, writer.Write(Frame(1), ptsUs: 1000));
        Assert.Equal(2ul, writer.Write(Frame(2), ptsUs: 2000));

        Assert.True(reader.TryReadLatest(0, buffer, out var frame));
        Assert.Equal(new VideoFrameInfo(2, 2000), frame);
        Assert.Equal(Frame(2), buffer);
    }

    [Fact]
    public void Latest_wins_across_slot_wrap_and_nothing_newer_returns_false()
    {
        var name = VideoRingLayout.RegionName(Guid.NewGuid().ToString("N"), 1);
        using var writer = VideoRingWriter.Create(name, width: 3, height: 3, PixelFormat.Bgra8);
        using var reader = VideoRingReader.Open(name);
        var buffer = new byte[reader.FrameSize];

        for (byte i = 1; i <= 7; i++)
        {
            writer.Write(Frame(i, size: 36), i * 10);
        }

        Assert.True(reader.TryReadLatest(0, buffer, out var frame));
        Assert.Equal(7ul, frame.Seq); // frames 1..6 skipped
        Assert.Equal(Frame(7, size: 36), buffer);
        Assert.False(reader.TryReadLatest(7, buffer, out _));
        Assert.Equal(0, reader.FramesTorn);
    }

    [Fact]
    public void Wrong_frame_size_is_rejected()
    {
        var name = VideoRingLayout.RegionName(Guid.NewGuid().ToString("N"), 2);
        using var writer = VideoRingWriter.Create(name, width: 4, height: 2, PixelFormat.Bgra8);

        Assert.Throws<ArgumentException>(() => writer.Write(new byte[31], 0));
    }

    [Fact]
    public void Audio_region_is_not_a_video_region()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        using var audio = AudioRingWriter.Create(AudioRingLayout.RegionName(sessionId), 1, 16_000, 16_000, 0);

        Assert.Throws<InvalidDataException>(() => VideoRingReader.Open(AudioRingLayout.RegionName(sessionId)));
    }

    private static byte[] Frame(byte value, int size = 32) => Enumerable.Repeat(value, size).ToArray();
}
