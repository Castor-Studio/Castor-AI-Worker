using System.IO.MemoryMappedFiles;

namespace Castor.AI.Contracts.Media;

/// <summary>
/// Shared memory audio ring buffer, layout documented in docs/architecture.md (section 4).
/// <code>
/// AudioHeader (64 B)
///   0   u32  magic          = 'CSAU'
///   4   u16  version        = 1
///   6   u16  channels
///   8   u32  sample_rate
///   12  u32  capacity_frames
///   16  u64  write_frame    (total frames written, published last)
///   24  i64  base_pts_us    (pts of frame 0)
/// Data: float32 interleaved, capacity_frames * channels
/// </code>
/// </summary>
public static class AudioRingLayout
{
    public const int HeaderSize = 64;
    public const uint Magic = 0x55415343; // "CSAU" little-endian
    public const ushort Version = 1;

    internal const int MagicOffset = 0;
    internal const int VersionOffset = 4;
    internal const int ChannelsOffset = 6;
    internal const int SampleRateOffset = 8;
    internal const int CapacityOffset = 12;
    internal const int WriteFrameOffset = 16;
    internal const int BasePtsOffset = 24;

    public static long SizeBytes(int channels, int capacityFrames) =>
        HeaderSize + (long)channels * capacityFrames * sizeof(float);

    public static string RegionName(string sessionId) =>
        OperatingSystem.IsWindows() ? $"Local\\castor-{sessionId}-audio" : $"castor-{sessionId}-audio";
}

/// <summary>Single-writer side (app). Not thread-safe: one producer thread.</summary>
public sealed unsafe class AudioRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly float* _data;
    private long _writeFrame;
    private bool _disposed;

    private AudioRingWriter(string name, int channels, int sampleRate, int capacityFrames, long basePtsUs)
    {
        Name = name;
        Channels = channels;
        SampleRate = sampleRate;
        CapacityFrames = capacityFrames;
        SizeBytes = AudioRingLayout.SizeBytes(channels, capacityFrames);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named shared memory is only implemented on Windows");
        }

        _file = MemoryMappedFile.CreateNew(name, SizeBytes);
        _view = _file.CreateViewAccessor(0, SizeBytes);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + _view.PointerOffset;
        _data = (float*)(_base + AudioRingLayout.HeaderSize);

        *(uint*)(_base + AudioRingLayout.MagicOffset) = AudioRingLayout.Magic;
        *(ushort*)(_base + AudioRingLayout.VersionOffset) = AudioRingLayout.Version;
        *(ushort*)(_base + AudioRingLayout.ChannelsOffset) = (ushort)channels;
        *(uint*)(_base + AudioRingLayout.SampleRateOffset) = (uint)sampleRate;
        *(uint*)(_base + AudioRingLayout.CapacityOffset) = (uint)capacityFrames;
        *(long*)(_base + AudioRingLayout.BasePtsOffset) = basePtsUs;
        Volatile.Write(ref *(long*)(_base + AudioRingLayout.WriteFrameOffset), 0);
    }

    public string Name { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int CapacityFrames { get; }

    public long SizeBytes { get; }

    public long WriteFrame => _writeFrame;

    public static AudioRingWriter Create(string name, int channels, int sampleRate, int capacityFrames, long basePtsUs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        return new AudioRingWriter(name, channels, sampleRate, capacityFrames, basePtsUs);
    }

    /// <summary>Appends interleaved frames, then publishes the new write position.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interleaved.Length % Channels != 0)
        {
            throw new ArgumentException($"Sample count must be a multiple of {Channels} channels", nameof(interleaved));
        }

        var frames = interleaved.Length / Channels;
        if (frames > CapacityFrames)
        {
            throw new ArgumentException("Block larger than the ring capacity", nameof(interleaved));
        }

        var start = (int)(_writeFrame % CapacityFrames);
        var firstFrames = Math.Min(frames, CapacityFrames - start);
        interleaved[..(firstFrames * Channels)].CopyTo(new Span<float>(_data + start * Channels, firstFrames * Channels));
        if (firstFrames < frames)
        {
            interleaved[(firstFrames * Channels)..].CopyTo(new Span<float>(_data, (frames - firstFrames) * Channels));
        }

        _writeFrame += frames;
        Volatile.Write(ref *(long*)(_base + AudioRingLayout.WriteFrameOffset), _writeFrame);
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

/// <summary>Reader side (worker).</summary>
public sealed unsafe class AudioRingReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly float* _data;
    private bool _disposed;

    private AudioRingReader(string name)
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
            var magic = *(uint*)(_base + AudioRingLayout.MagicOffset);
            var version = *(ushort*)(_base + AudioRingLayout.VersionOffset);
            if (magic != AudioRingLayout.Magic || version != AudioRingLayout.Version)
            {
                throw new InvalidDataException($"Not a v{AudioRingLayout.Version} audio ring: magic 0x{magic:X8}, version {version}");
            }

            Channels = *(ushort*)(_base + AudioRingLayout.ChannelsOffset);
            SampleRate = (int)*(uint*)(_base + AudioRingLayout.SampleRateOffset);
            CapacityFrames = (int)*(uint*)(_base + AudioRingLayout.CapacityOffset);
            BasePtsUs = *(long*)(_base + AudioRingLayout.BasePtsOffset);
            _data = (float*)(_base + AudioRingLayout.HeaderSize);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Channels { get; }

    public int SampleRate { get; }

    public int CapacityFrames { get; }

    public long BasePtsUs { get; }

    public long WriteFrame => Volatile.Read(ref *(long*)(_base + AudioRingLayout.WriteFrameOffset));

    public static AudioRingReader Open(string name) => new(name);

    /// <summary>
    /// Copies frames [fromFrame, fromFrame + frameCount) as interleaved samples.
    /// Returns false when that range was (or may have been) overwritten by the writer.
    /// </summary>
    public bool TryRead(long fromFrame, int frameCount, Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < frameCount * Channels)
        {
            throw new ArgumentException("Destination too small", nameof(destination));
        }

        var published = WriteFrame;
        if (fromFrame < 0 || fromFrame + frameCount > published || published - fromFrame > CapacityFrames)
        {
            return false;
        }

        var start = (int)(fromFrame % CapacityFrames);
        var firstFrames = Math.Min(frameCount, CapacityFrames - start);
        new ReadOnlySpan<float>(_data + start * Channels, firstFrames * Channels).CopyTo(destination);
        if (firstFrames < frameCount)
        {
            new ReadOnlySpan<float>(_data, (frameCount - firstFrames) * Channels)
                .CopyTo(destination[(firstFrames * Channels)..]);
        }

        // The writer may have lapped us during the copy.
        return WriteFrame - fromFrame <= CapacityFrames;
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
