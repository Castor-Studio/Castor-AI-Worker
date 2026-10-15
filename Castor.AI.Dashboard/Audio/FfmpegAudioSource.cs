using System.Diagnostics;
using Castor.AI;

namespace Castor.AI.Dashboard.Audio;

/// <summary>
/// Test microphone: decodes the audio track of any file ffmpeg understands (wav, mp3, mp4, mkv...)
/// to 16 kHz mono float32, in real time (<c>-re</c>), looped, and writes it to
/// <see cref="AiClient.WriteAudio"/>. Castor Studio does the same from its OBS audio sources.
/// </summary>
public sealed class FfmpegAudioSource : IDisposable
{
    private const string FfmpegEnvironmentVariable = "CASTOR_FFMPEG";
    private const int SampleRate = 16_000;

    private readonly Process _process;
    private readonly Thread _reader;
    private readonly AiClient _client;
    private readonly int _channel;
    private volatile bool _running = true;

    public FfmpegAudioSource(string path, int channel, AiClient client, Action<string> log)
    {
        _client = client;
        _channel = channel;
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
                     "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1",
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
                $"ffmpeg not found ({ex.Message}). Install it or set {FfmpegEnvironmentVariable}.", ex);
        }

        _process.BeginErrorReadLine();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ffmpeg-mic{channel}" };
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
        var bytes = new byte[4096];
        var samples = new float[bytes.Length / sizeof(float)];
        var pending = 0;
        try
        {
            while (_running)
            {
                var read = stream.Read(bytes, pending, bytes.Length - pending);
                if (read == 0)
                {
                    return;
                }

                pending += read;
                var count = pending / sizeof(float);
                Buffer.BlockCopy(bytes, 0, samples, 0, count * sizeof(float));
                _client.WriteAudio(_channel, samples.AsSpan(0, count), SampleRate);

                var consumed = count * sizeof(float);
                pending -= consumed;
                Array.Copy(bytes, consumed, bytes, 0, pending);
            }
        }
        catch (Exception) when (!_running)
        {
            // Process killed on dispose.
        }
        catch (IOException)
        {
        }
    }
}
