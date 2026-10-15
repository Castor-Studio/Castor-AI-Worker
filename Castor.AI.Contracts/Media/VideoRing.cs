using System.IO.MemoryMappedFiles;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Contracts.Media;

/// <summary>
/// Shared memory video region (one per camera), layout documented in docs/architecture.md (section 4).
/// <code>
/// RegionHeader (64 B)
///   0   u32  magic        = 'CSVF'
///   4   u16  version      = 1
///   6   u16  slot_count   (>= 3)
///   8   u32  slot_size    (slot header included, multiple of 64)
///   12  u32  width
///   16  u32  height
///   20  u32  stride
///   24  u32  pixel_format (enum PixelFormat)
///   32  u64  latest_seq   (0 = no frame yet, published last)
/// Slot[i] at 64 + i * slot_size
///   0   u64  seq          (0 while being written, published last)
///   8   i64  pts_us
///   16  u32  data_size
///   64  data
/// </code>
/// </summary>
public static class VideoRingLayout
{
    public const int HeaderSize = 64;
    public const int SlotHeaderSize = 64;
    public const uint Magic = 0x46565343; // "CSVF" little-endian
    public const ushort Version = 1;
    public const int DefaultSlotCount = 3;

    internal const int MagicOffset = 0;
    internal const int VersionOffset = 4;
    internal const int SlotCountOffset = 6;
    internal const int SlotSizeOffset = 8;
    internal const int WidthOffset = 12;
    internal const int HeightOffset = 16;
    internal const int StrideOffset = 20;
    internal const int PixelFormatOffset = 24;
    internal const int LatestSeqOffset = 32;

    internal const int SlotSeqOffset = 0;
    internal const int SlotPtsOffset = 8;
    internal const int SlotDataSizeOffset = 16;

    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Bgra8 => 4,
        PixelFormat.Rgb24 => 3,
        _ => throw new NotSupportedException($"Pixel format {format} not supported in shared memory"),
    };

    public static int SlotSize(int stride, int height) => Align64(SlotHeaderSize + stride * height);

    public static long SizeBytes(int slotCount, int slotSize) => HeaderSize + (long)slotCount * slotSize;

    /// <summary>
    /// OS name of the region of camera number <paramref name="cameraIndex"/>. The index, not the camera id,
    /// keeps the name valid whatever the app uses as ids; <c>VideoRegion.camera_id</c> carries the mapping.
    /// </summary>
    public static string RegionName(string sessionId, int cameraIndex) =>
        OperatingSystem.IsWindows()
            ? $"Local\\castor-{sessionId}-video-{cameraIndex}"
            : $"castor-{sessionId}-video-{cameraIndex}";

    private static int Align64(int value) => (value + 63) & ~63;
}

/// <summary>A frame copied out of a <see cref="VideoRingReader"/>.</summary>
public readonly record struct VideoFrameInfo(ulong Seq, long PtsUs);

/// <summary>Single-writer side (app). Not thread-safe: one producer per camera.</summary>
public sealed unsafe class VideoRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private ulong _seq;
    private bool _disposed;

    private VideoRingWriter(string name, int width, int height, PixelFormat format, int slotCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named shared memory is only implemented on Windows");
        }

        Name = name;
        Width = width;
        Height = height;
        PixelFormat = format;
        SlotCount = slotCount;
        Stride = width * VideoRingLayout.BytesPerPixel(format);
        SlotSize = VideoRingLayout.SlotSize(Stride, height);
        SizeBytes = VideoRingLayout.SizeBytes(slotCount, SlotSize);

        _file = MemoryMappedFile.CreateNew(name, SizeBytes);
        _view = _file.CreateViewAccessor(0, SizeBytes);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;

        *(uint*)(_base + VideoRingLayout.MagicOffset) = VideoRingLayout.Magic;
        *(ushort*)(_base + VideoRingLayout.VersionOffset) = VideoRingLayout.Version;
        *(ushort*)(_base + VideoRingLayout.SlotCountOffset) = (ushort)slotCount;
        *(uint*)(_base + VideoRingLayout.SlotSizeOffset) = (uint)SlotSize;
        *(uint*)(_base + VideoRingLayout.WidthOffset) = (uint)width;
        *(uint*)(_base + VideoRingLayout.HeightOffset) = (uint)height;
        *(uint*)(_base + VideoRingLayout.StrideOffset) = (uint)Stride;
        *(uint*)(_base + VideoRingLayout.PixelFormatOffset) = (uint)format;
        Volatile.Write(ref *(ulong*)(_base + VideoRingLayout.LatestSeqOffset), 0);
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public PixelFormat PixelFormat { get; }

    public int SlotCount { get; }

    public int SlotSize { get; }

    public long SizeBytes { get; }

    /// <summary>Seq of the last published frame (0 = none).</summary>
    public ulong LatestSeq => _seq;

    public static VideoRingWriter Create(
        string name, int width, int height, PixelFormat format, int slotCount = VideoRingLayout.DefaultSlotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 3);
        return new VideoRingWriter(name, width, height, format, slotCount);
    }

    /// <summary>Writes one frame (exactly <see cref="Stride"/> * <see cref="Height"/> bytes), returns its seq.</summary>
    public ulong Write(ReadOnlySpan<byte> pixels, long ptsUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dataSize = Stride * Height;
        if (pixels.Length != dataSize)
        {
            throw new ArgumentException($"Expected {dataSize} bytes ({Width}x{Height} {PixelFormat}), got {pixels.Length}", nameof(pixels));
        }

        var seq = _seq + 1;
        var slot = _base + VideoRingLayout.HeaderSize + (long)(seq % (ulong)SlotCount) * SlotSize;

        // Invalidate the slot while it is being written: a reader copying it sees the seq change.
        Volatile.Write(ref *(ulong*)(slot + VideoRingLayout.SlotSeqOffset), 0);
        *(long*)(slot + VideoRingLayout.SlotPtsOffset) = ptsUs;
        *(uint*)(slot + VideoRingLayout.SlotDataSizeOffset) = (uint)dataSize;
        pixels.CopyTo(new Span<byte>(slot + VideoRingLayout.SlotHeaderSize, dataSize));
        Volatile.Write(ref *(ulong*)(slot + VideoRingLayout.SlotSeqOffset), seq);
        Volatile.Write(ref *(ulong*)(_base + VideoRingLayout.LatestSeqOffset), seq);

        _seq = seq;
        return seq;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>Reader side (worker). Latest-wins: only the newest frame is ever read.</summary>
public sealed unsafe class VideoRingReader : IDisposable
{
    private const int MaxAttempts = 3;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private bool _disposed;

    private VideoRingReader(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux needs a /dev/shm file-backed mapping instead of a named one (cloud, not done yet).
            throw new PlatformNotSupportedException("Named shared memory is only implemented on Windows");
        }

        _file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;

        try
        {
            var magic = *(uint*)(_base + VideoRingLayout.MagicOffset);
            var version = *(ushort*)(_base + VideoRingLayout.VersionOffset);
            if (magic != VideoRingLayout.Magic || version != VideoRingLayout.Version)
            {
                throw new InvalidDataException($"Not a v{VideoRingLayout.Version} video region: magic 0x{magic:X8}, version {version}");
            }

            SlotCount = *(ushort*)(_base + VideoRingLayout.SlotCountOffset);
            SlotSize = (int)*(uint*)(_base + VideoRingLayout.SlotSizeOffset);
            Width = (int)*(uint*)(_base + VideoRingLayout.WidthOffset);
            Height = (int)*(uint*)(_base + VideoRingLayout.HeightOffset);
            Stride = (int)*(uint*)(_base + VideoRingLayout.StrideOffset);
            PixelFormat = (PixelFormat)(int)*(uint*)(_base + VideoRingLayout.PixelFormatOffset);
            if (SlotCount < 3 || Width < 1 || Height < 1 || Stride < Width * VideoRingLayout.BytesPerPixel(PixelFormat)
                || (long)VideoRingLayout.HeaderSize + (long)SlotCount * SlotSize > _view.Capacity)
            {
                throw new InvalidDataException(
                    $"Inconsistent video region header: {Width}x{Height} stride {Stride}, {SlotCount} slots of {SlotSize} B");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public PixelFormat PixelFormat { get; }

    public int SlotCount { get; }

    public int SlotSize { get; }

    /// <summary>Bytes of one frame: <see cref="Stride"/> * <see cref="Height"/>.</summary>
    public int FrameSize => Stride * Height;

    public ulong LatestSeq => Volatile.Read(ref *(ulong*)(_base + VideoRingLayout.LatestSeqOffset));

    /// <summary>Frames rejected because the writer overwrote them during the copy.</summary>
    public long FramesTorn { get; private set; }

    public static VideoRingReader Open(string name) => new(name);

    /// <summary>
    /// Copies the newest frame into <paramref name="destination"/> (at least <see cref="FrameSize"/> bytes)
    /// when its seq is greater than <paramref name="afterSeq"/>. False: nothing newer, or every attempt was torn.
    /// </summary>
    public bool TryReadLatest(ulong afterSeq, Span<byte> destination, out VideoFrameInfo frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < FrameSize)
        {
            throw new ArgumentException("Destination too small", nameof(destination));
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var seq = LatestSeq;
            if (seq == 0 || seq <= afterSeq)
            {
                break;
            }

            var slot = _base + VideoRingLayout.HeaderSize + (long)(seq % (ulong)SlotCount) * SlotSize;
            if (Volatile.Read(ref *(ulong*)(slot + VideoRingLayout.SlotSeqOffset)) != seq)
            {
                FramesTorn++;
                continue;
            }

            var ptsUs = *(long*)(slot + VideoRingLayout.SlotPtsOffset);
            var dataSize = (int)Math.Min(*(uint*)(slot + VideoRingLayout.SlotDataSizeOffset), (uint)FrameSize);
            new ReadOnlySpan<byte>(slot + VideoRingLayout.SlotHeaderSize, dataSize).CopyTo(destination);

            // The writer may have lapped us during the copy.
            if (Volatile.Read(ref *(ulong*)(slot + VideoRingLayout.SlotSeqOffset)) != seq)
            {
                FramesTorn++;
                continue;
            }

            frame = new VideoFrameInfo(seq, ptsUs);
            return true;
        }

        frame = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
