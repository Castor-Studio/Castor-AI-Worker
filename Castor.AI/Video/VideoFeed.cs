using Castor.AI.Contracts.V1;

namespace Castor.AI.Video;

internal delegate bool VideoFrameSink(string cameraId, ReadOnlySpan<byte> pixels, long ptsUs);

/// <summary>
/// Turns camera frames of any size (BGRA, any thread, any rate) into what the worker asked for in
/// <see cref="WorkerCapabilities.PreferredVideo"/>: scaled to its size, tightly packed, and no faster
/// than its fps. Frames above the rate are dropped here, before any copy (latest-wins on the worker side).
/// </summary>
internal sealed class VideoFeed
{
    // Accept frames slightly early so a source running exactly at the target fps is not halved by jitter.
    private const double RateTolerance = 0.8;

    private readonly Dictionary<string, CameraState> _cameras;
    private readonly VideoFrameSink _sink;
    private readonly long _minIntervalUs;

    public VideoFeed(VideoFormat format, IEnumerable<string> cameraIds, VideoFrameSink sink)
    {
        Format = format;
        _sink = sink;
        _minIntervalUs = format.Fps > 0 ? (long)(1_000_000 / format.Fps * RateTolerance) : 0;
        _cameras = cameraIds.ToDictionary(id => id, _ => new CameraState((int)format.Width * (int)format.Height * 4));
    }

    public VideoFormat Format { get; }

    /// <summary>
    /// The worker reads video when it declares the input with a BGRA preferred format (the only one the SDK produces).
    /// </summary>
    public static VideoFormat? WantedFormat(WorkerCapabilities capabilities)
    {
        var wanted = capabilities.RequiredInputs.Contains(InputKind.Video)
                     || capabilities.OptionalInputs.Contains(InputKind.Video);
        if (!wanted)
        {
            return null;
        }

        if (capabilities.PreferredVideo is not { Width: > 0, Height: > 0 } video || video.PixelFormat != PixelFormat.Bgra8)
        {
            throw new NotSupportedException(
                $"Worker video format not supported by the SDK: {capabilities.PreferredVideo} (BGRA8 with a size expected)");
        }

        return video;
    }

    /// <returns>False when the camera is unknown, the frame comes too early, or no session runs.</returns>
    public bool Write(string cameraId, ReadOnlySpan<byte> bgra, int width, int height, int stride, long nowUs)
    {
        if (!_cameras.TryGetValue(cameraId, out var camera))
        {
            return false;
        }

        stride = stride == 0 ? width * 4 : stride;
        if (width < 1 || height < 1 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException($"Invalid BGRA frame: {width}x{height}, stride {stride}, {bgra.Length} bytes", nameof(bgra));
        }

        lock (camera)
        {
            if (camera.LastUs is { } last && nowUs - last < _minIntervalUs)
            {
                return false;
            }

            camera.LastUs = nowUs;
            BgraScaler.Scale(bgra, width, height, stride, camera.Buffer, (int)Format.Width, (int)Format.Height);
            return _sink(cameraId, camera.Buffer, nowUs);
        }
    }

    private sealed class CameraState(int size)
    {
        public byte[] Buffer { get; } = new byte[size];

        public long? LastUs { get; set; }
    }
}

/// <summary>Bilinear BGRA resize, same sampling as OpenCV <c>INTER_LINEAR</c> (pixel centers aligned).</summary>
internal static class BgraScaler
{
    public static void Scale(
        ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride,
        Span<byte> destination, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            for (var y = 0; y < height; y++)
            {
                source.Slice(y * sourceStride, width * 4).CopyTo(destination.Slice(y * width * 4, width * 4));
            }

            return;
        }

        var scaleX = (double)sourceWidth / width;
        var scaleY = (double)sourceHeight / height;
        Span<int> x0 = width <= 4096 ? stackalloc int[width] : new int[width];
        Span<int> x1 = width <= 4096 ? stackalloc int[width] : new int[width];
        Span<float> fx = width <= 4096 ? stackalloc float[width] : new float[width];
        for (var x = 0; x < width; x++)
        {
            Sample((x + 0.5) * scaleX - 0.5, sourceWidth, out x0[x], out x1[x], out fx[x]);
        }

        for (var y = 0; y < height; y++)
        {
            Sample((y + 0.5) * scaleY - 0.5, sourceHeight, out var y0, out var y1, out var fy);
            var row0 = source.Slice(y0 * sourceStride);
            var row1 = source.Slice(y1 * sourceStride);
            var output = destination.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var a = x0[x] * 4;
                var b = x1[x] * 4;
                var wx = fx[x];
                for (var c = 0; c < 4; c++)
                {
                    var top = row0[a + c] + (row0[b + c] - row0[a + c]) * wx;
                    var bottom = row1[a + c] + (row1[b + c] - row1[a + c]) * wx;
                    output[x * 4 + c] = (byte)(top + (bottom - top) * fy + 0.5f);
                }
            }
        }
    }

    private static void Sample(double position, int size, out int low, out int high, out float weight)
    {
        if (position <= 0)
        {
            low = high = 0;
            weight = 0;
            return;
        }

        low = (int)position;
        if (low >= size - 1)
        {
            low = high = size - 1;
            weight = 0;
            return;
        }

        high = low + 1;
        weight = (float)(position - low);
    }
}
