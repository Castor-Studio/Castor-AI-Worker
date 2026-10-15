using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker;

/// <summary>Media regions mapped for one session.</summary>
public sealed class SessionMedia : IDisposable
{
    private SessionMedia(AudioRingReader? audio, IReadOnlyDictionary<string, VideoRingReader> video)
    {
        Audio = audio;
        Video = video;
    }

    public AudioRingReader? Audio { get; }

    /// <summary>camera_id -> video region. Empty when the app sends no video.</summary>
    public IReadOnlyDictionary<string, VideoRingReader> Video { get; }

    public static SessionMedia Open(MediaTransport? transport)
    {
        if (transport is null || transport.KindCase == MediaTransport.KindOneofCase.None)
        {
            return new SessionMedia(null, new Dictionary<string, VideoRingReader>());
        }

        if (transport.KindCase != MediaTransport.KindOneofCase.SharedMemory)
        {
            throw new NotSupportedException($"Transport {transport.KindCase} not supported by the local worker");
        }

        var video = new Dictionary<string, VideoRingReader>();
        AudioRingReader? audio = null;
        try
        {
            foreach (var region in transport.SharedMemory.Video)
            {
                if (!video.TryAdd(region.CameraId, VideoRingReader.Open(region.Name)))
                {
                    throw new InvalidDataException($"Two video regions for camera '{region.CameraId}'");
                }
            }

            var audioName = transport.SharedMemory.Audio?.Name;
            audio = string.IsNullOrEmpty(audioName) ? null : AudioRingReader.Open(audioName);
            return new SessionMedia(audio, video);
        }
        catch
        {
            foreach (var reader in video.Values)
            {
                reader.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        Audio?.Dispose();
        foreach (var reader in Video.Values)
        {
            reader.Dispose();
        }
    }
}
