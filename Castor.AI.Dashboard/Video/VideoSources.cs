using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Castor.AI;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Dashboard.Video;

/// <summary>
/// Test camera for <see cref="AiClient.WriteVideo"/>. Castor Studio does the same from its OBS scenes.
/// <see cref="Open"/> picks the source from the file: still image (looped) or any video ffmpeg reads.
/// </summary>
public static class VideoSource
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    public static IDisposable Open(string path, string cameraId, VideoFormat format, AiClient client, Action<string> log,
        Action<byte[], int, int>? preview = null) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? new ImageVideoSource(path, cameraId, format, client, preview)
            : new FfmpegVideoSource(path, cameraId, format, client, log, preview);
}

/// <summary>A still image sent at the worker fps. Decoded and scaled once by Avalonia.</summary>
public sealed class ImageVideoSource : IDisposable
{
    private readonly Timer _timer;

    public ImageVideoSource(string path, string cameraId, VideoFormat format, AiClient client,
        Action<byte[], int, int>? preview = null)
    {
        var width = (int)format.Width;
        var height = (int)format.Height;
        using var source = new Bitmap(path);
        using var scaled = source.CreateScaledBitmap(new PixelSize(width, height));
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            scaled.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        if (scaled.Format != PixelFormats.Bgra8888)
        {
            // Rgba8888: swap to the BGRA the SDK expects.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
        }

        var period = TimeSpan.FromSeconds(1 / (format.Fps > 0 ? format.Fps : 15));
        _timer = new Timer(_ =>
        {
            if (client.WriteVideo(cameraId, pixels, width, height)) preview?.Invoke(pixels, width, height);
        }, null, TimeSpan.Zero, period);
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>
/// Decodes any file ffmpeg understands to raw BGRA at the worker size and fps, in real time (<c>-re</c>), looped.
/// </summary>
public sealed class FfmpegVideoSource : IDisposable
{
    private const string FfmpegEnvironmentVariable = "CASTOR_FFMPEG";

    private readonly Process _process;
    private readonly Thread _reader;
    private readonly AiClient _client;
    private readonly string _cameraId;
    private readonly int _width;
    private readonly int _height;
    private readonly Action<byte[], int, int>? _preview;
    private volatile bool _running = true;

    public FfmpegVideoSource(string path, string cameraId, VideoFormat format, AiClient client, Action<string> log,
        Action<byte[], int, int>? preview = null)
    {
        _client = client;
        _cameraId = cameraId;
        _width = (int)format.Width;
        _height = (int)format.Height;
        _preview = preview;
        var fps = format.Fps > 0 ? format.Fps : 15;
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable(FfmpegEnvironmentVariable) ?? "ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-re", "-stream_loop", "-1", "-i", path,
                     "-an", "-vf", FormattableString.Invariant($"fps={fps},scale={_width}:{_height}"),
                     "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log($"[ffmpeg {Path.GetFileName(path)}] {e.Data}");
            }
        };

        try
        {
            _process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"ffmpeg not found ({ex.Message}). Install it, set {FfmpegEnvironmentVariable}, or use a still image.", ex);
        }

        _process.BeginErrorReadLine();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ffmpeg-video-{cameraId}" };
        _reader.Start();
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _reader.Join(TimeSpan.FromSeconds(1));
        _process.Dispose();
    }

    private void ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        var frame = new byte[_width * _height * 4];
        try
        {
            while (_running)
            {
                stream.ReadExactly(frame);
                if (_client.WriteVideo(_cameraId, frame, _width, _height))
                    _preview?.Invoke(frame, _width, _height);
            }
        }
        catch (Exception) when (!_running)
        {
            // Process killed on dispose.
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
    }
}
