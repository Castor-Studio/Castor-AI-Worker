using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Castor.AI.Dashboard.Video;

/// <summary>
/// Keeps only the latest submitted frame. Producers copy into one bounded buffer; the UI
/// uploads it on its own timer, so decoding never queues UI work or creates a bitmap per frame.
/// Start, Refresh and Stop are called on the UI thread; Publish may run on any thread.
/// </summary>
public sealed partial class VideoPreview : ObservableObject
{
    private readonly object _gate = new();
    private byte[]? _pixels;
    private bool _pending;
    private int _width;
    private int _height;
    private long _generation;

    public WriteableBitmap? Bitmap { get; private set; }

    [ObservableProperty]
    private bool _hasFrame;

    public event Action? Changed;

    public Action<byte[], int, int> Start(int width, int height)
    {
        Stop();
        lock (_gate)
        {
            _width = width;
            _height = height;
            _pixels = new byte[checked(width * height * 4)];
            var generation = _generation;
            return (pixels, frameWidth, frameHeight) => Publish(pixels, frameWidth, frameHeight, generation);
        }
    }

    private void Publish(byte[] pixels, int width, int height, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _pixels is null || width != _width || height != _height || pixels.Length != _pixels.Length)
                return;

            pixels.CopyTo(_pixels, 0);
            _pending = true;
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (!_pending || _pixels is null) return;

            Bitmap ??= new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96),
                PixelFormats.Bgra8888, AlphaFormat.Opaque);
            using var target = Bitmap.Lock();
            var stride = _width * 4;
            for (var row = 0; row < _height; row++)
                Marshal.Copy(_pixels, row * stride, IntPtr.Add(target.Address, row * target.RowBytes), stride);
            _pending = false;
        }

        HasFrame = true;
        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _pixels = null;
            _pending = false;
            _generation++;
        }

        Bitmap?.Dispose();
        Bitmap = null;
        HasFrame = false;
        Changed?.Invoke();
    }
}
