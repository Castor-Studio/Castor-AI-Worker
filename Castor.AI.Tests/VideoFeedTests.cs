using Castor.AI.Contracts.V1;
using Castor.AI.Video;

namespace Castor.AI.Tests;

public class VideoFeedTests
{
    private static readonly VideoFormat Format = new() { Width = 4, Height = 2, PixelFormat = PixelFormat.Bgra8, Fps = 10 };

    [Fact]
    public void Same_size_frame_is_copied_without_its_row_padding()
    {
        var pushed = new List<byte[]>();
        var feed = new VideoFeed(Format, ["cam"], Sink(pushed));
        var source = new byte[2 * 20]; // stride 20 = 16 pixel bytes + 4 padding
        for (var i = 0; i < 16; i++)
        {
            source[i] = (byte)i;
            source[20 + i] = (byte)(100 + i);
        }

        Assert.True(feed.Write("cam", source, 4, 2, stride: 20, nowUs: 0));

        var frame = Assert.Single(pushed);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).Concat(Enumerable.Range(100, 16).Select(i => (byte)i)), frame);
    }

    [Fact]
    public void Downscale_by_two_averages_pixel_pairs_like_opencv_linear()
    {
        var pushed = new List<byte[]>();
        var feed = new VideoFeed(Format, ["cam"], Sink(pushed));
        // 8x4 source: column x holds x * 10 in every channel.
        var source = new byte[8 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    source[(y * 8 + x) * 4 + c] = (byte)(x * 10);
                }
            }
        }

        feed.Write("cam", source, 8, 4, stride: 0, nowUs: 0);

        // Destination pixel x samples source position 2x + 0.5: mean of columns 2x and 2x + 1.
        var frame = Assert.Single(pushed);
        Assert.Equal([5, 25, 45, 65], Enumerable.Range(0, 4).Select(x => (int)frame[x * 4]));
    }

    [Fact]
    public void Frames_faster_than_the_worker_fps_are_dropped_per_camera()
    {
        var pushed = new List<byte[]>();
        var feed = new VideoFeed(Format, ["a", "b"], Sink(pushed));
        var frame = new byte[4 * 2 * 4];

        Assert.True(feed.Write("a", frame, 4, 2, 0, nowUs: 0));
        Assert.False(feed.Write("a", frame, 4, 2, 0, nowUs: 50_000)); // 20 fps > 10 fps
        Assert.True(feed.Write("b", frame, 4, 2, 0, nowUs: 50_000)); // other camera, own pace
        Assert.True(feed.Write("a", frame, 4, 2, 0, nowUs: 100_000));
        Assert.False(feed.Write("unknown", frame, 4, 2, 0, nowUs: 200_000));
        Assert.Equal(3, pushed.Count);
    }

    [Fact]
    public void Truncated_frame_is_rejected()
    {
        var feed = new VideoFeed(Format, ["cam"], Sink([]));

        Assert.Throws<ArgumentException>(() => feed.Write("cam", new byte[10], 4, 2, 0, 0));
    }

    [Fact]
    public void Worker_wants_video_only_when_it_declares_the_input()
    {
        Assert.Null(VideoFeed.WantedFormat(new WorkerCapabilities { PreferredVideo = Format }));
        Assert.Equal(Format, VideoFeed.WantedFormat(new WorkerCapabilities { RequiredInputs = { InputKind.Video }, PreferredVideo = Format }));
        Assert.Throws<NotSupportedException>(() => VideoFeed.WantedFormat(new WorkerCapabilities
        {
            OptionalInputs = { InputKind.Video },
            PreferredVideo = new VideoFormat { Width = 4, Height = 2, PixelFormat = PixelFormat.Nv12 },
        }));
    }

    [Fact]
    public async Task Client_writes_video_only_to_a_worker_that_takes_it()
    {
        var provider = new FakeProvider
        {
            Capabilities = new WorkerCapabilities { WorkerId = "fake-1", RequiredInputs = { InputKind.Video }, PreferredVideo = Format },
        };
        await using var client = new AiClient(provider);
        var frame = new byte[8 * 4 * 4];
        Assert.False(client.WriteVideo("left", frame, 8, 4)); // no session

        await client.StartAsync(new AiSessionOptions
        {
            Domain = "football",
            Cameras = [new AiCamera("left", "Left"), new AiCamera("right", "Right")],
        });

        Assert.Equal(Format, client.VideoFormat);
        Assert.True(client.WriteVideo("left", frame, 8, 4));
        Assert.Equal(4 * 2 * 4, provider.LastVideo["left"].Length); // scaled to the worker format
        Assert.False(client.WriteVideo("missing", frame, 8, 4));

        await client.StopAsync();
        Assert.Null(client.VideoFormat);
        Assert.False(client.WriteVideo("left", frame, 8, 4));
    }

    [Fact]
    public async Task Podcast_like_worker_gets_no_video()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(new AiSessionOptions { Domain = "podcast", Cameras = [new AiCamera("host", "Host")] });

        Assert.Null(client.VideoFormat);
        Assert.False(client.WriteVideo("host", new byte[16], 2, 2));
        Assert.Empty(provider.VideoFrames);
    }

    private static VideoFrameSink Sink(List<byte[]> pushed) => (_, pixels, _) =>
    {
        pushed.Add(pixels.ToArray());
        return true;
    };
}
