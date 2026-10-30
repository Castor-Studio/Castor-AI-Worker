using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Castor.AI.Dashboard.Video;

namespace Castor.AI.Dashboard.Tests;

public class VideoPreviewTests
{
    static VideoPreviewTests() => SkiaPlatform.Initialize();

    [Fact]
    public void Preview_copies_producer_pixels_and_keeps_only_the_latest_frame()
    {
        var preview = new VideoPreview();
        var publish = preview.Start(2, 1);
        var first = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
        var latest = new byte[] { 7, 8, 9, 255, 10, 11, 12, 255 };
        publish(first, 2, 1);
        publish(latest, 2, 1);
        Array.Fill(latest, (byte)0); // Decoder reuses its source array immediately.

        Assert.False(preview.HasFrame);
        preview.Refresh();

        Assert.True(preview.HasFrame);
        Assert.Equal(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255 }, ReadPixels(preview.Bitmap!));
        preview.Stop();
    }

    [Fact]
    public void Frames_reuse_the_bitmap_and_notify_both_views_only_when_pixels_change()
    {
        var preview = new VideoPreview();
        var publish = preview.Start(1, 1);
        var notifications = 0;
        preview.Changed += () => notifications++;
        publish([1, 2, 3, 255], 1, 1);
        preview.Refresh();
        var bitmap = preview.Bitmap;
        preview.Refresh(); // No new frame.
        Assert.Equal(1, notifications);

        publish([4, 5, 6, 255], 1, 1);
        preview.Refresh();
        Assert.Same(bitmap, preview.Bitmap);
        Assert.Equal(2, notifications);
        Assert.Equal(new byte[] { 4, 5, 6, 255 }, ReadPixels(preview.Bitmap!));
        preview.Stop();
    }

    [Fact]
    public void Stop_clears_pending_frames_and_ignores_late_producer_callbacks()
    {
        var preview = new VideoPreview();
        var publish = preview.Start(1, 1);
        publish([1, 2, 3, 255], 1, 1);
        preview.Refresh();
        publish([4, 5, 6, 255], 1, 1);
        preview.Stop();
        publish([7, 8, 9, 255], 1, 1);
        preview.Refresh();

        Assert.False(preview.HasFrame);
        Assert.Null(preview.Bitmap);
    }

    [Fact]
    public void Restart_accepts_new_dimensions_and_drops_incompatible_frames()
    {
        var preview = new VideoPreview();
        var publish = preview.Start(1, 1);
        publish([1, 2, 3, 255], 1, 1);
        preview.Refresh();
        publish = preview.Start(1, 2);
        publish([1, 2, 3, 255], 1, 1);
        publish([1], 1, 2);
        preview.Refresh();
        Assert.False(preview.HasFrame);
        Assert.Null(preview.Bitmap);

        publish([1, 2, 3, 255, 4, 5, 6, 255], 1, 2);
        preview.Refresh();
        Assert.Equal(new PixelSize(1, 2), preview.Bitmap!.PixelSize);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, ReadPixels(preview.Bitmap));
        preview.Stop();
    }

    [Fact]
    public void Concurrent_producers_cannot_mix_pixels_from_different_frames()
    {
        var preview = new VideoPreview();
        var publish = preview.Start(32, 16);
        var frames = Enumerable.Range(1, 32)
            .Select(value => Enumerable.Repeat((byte)value, 32 * 16 * 4).ToArray()).ToArray();
        Parallel.ForEach(frames, frame => publish(frame, 32, 16));
        preview.Refresh();

        var pixels = ReadPixels(preview.Bitmap!);
        Assert.InRange(pixels[0], (byte)1, (byte)32);
        Assert.All(pixels, value => Assert.Equal(pixels[0], value));
        preview.Stop();
    }

    [Fact]
    public void Replacing_a_source_rejects_late_frames_even_with_the_same_dimensions()
    {
        var preview = new VideoPreview();
        var oldSource = preview.Start(1, 1);
        var newSource = preview.Start(1, 1);
        newSource([4, 5, 6, 255], 1, 1);
        oldSource([1, 2, 3, 255], 1, 1);
        preview.Refresh();

        Assert.Equal(new byte[] { 4, 5, 6, 255 }, ReadPixels(preview.Bitmap!));
        preview.Stop();
    }

    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        using var source = bitmap.Lock();
        var stride = bitmap.PixelSize.Width * 4;
        var pixels = new byte[stride * bitmap.PixelSize.Height];
        for (var row = 0; row < bitmap.PixelSize.Height; row++)
            Marshal.Copy(IntPtr.Add(source.Address, row * source.RowBytes), pixels, row * stride, stride);
        return pixels;
    }
}
