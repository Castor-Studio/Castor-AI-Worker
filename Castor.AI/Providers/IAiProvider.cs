using Castor.AI.Contracts.V1;

namespace Castor.AI.Providers;

/// <summary>Connection to the AI, as seen by the app.</summary>
public enum AiConnectionState
{
    Stopped,
    Starting,
    Running,
    Faulted,
    Restarting,
}

/// <summary>What a provider needs to open a worker session.</summary>
/// <param name="AudioChannels">Microphones in the audio ring (0 = no audio).</param>
/// <param name="ClockUs">Session clock, stamps the media.</param>
public sealed record AiProviderRequest(
    string Domain,
    IReadOnlyList<CameraDescriptor> Cameras,
    int AudioChannels,
    IReadOnlyDictionary<string, string> Parameters,
    Func<long> ClockUs);

/// <summary>
/// Where the AI runs: a local worker process (<see cref="Local.LocalWorkerProvider"/>), later the cloud.
/// Apps use <see cref="AiClient"/>; implement this only to add a new kind of backend.
/// Events may be raised on any thread.
/// </summary>
public interface IAiProvider : IAsyncDisposable
{
    string Name { get; }

    event Action<AiConnectionState, string>? StateChanged;

    /// <summary>Raised on every (re)start once the worker accepted the session.</summary>
    event Action<WorkerCapabilities>? SessionStarted;

    event Action<WorkerMessage>? MessageReceived;

    event Action<string>? Log;

    Task StartAsync(AiProviderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Queues a message for the worker. Returns false when no session is running.</summary>
    bool Send(ClientMessage message);

    /// <summary>
    /// Appends interleaved float32 frames (<see cref="AiProviderRequest.AudioChannels"/> channels, 16 kHz).
    /// Single producer thread. Returns false when no session is running.
    /// </summary>
    bool PushAudio(ReadOnlySpan<float> interleaved, long ptsUs);

    /// <summary>
    /// Publishes one frame of <paramref name="cameraId"/>, already in <see cref="WorkerCapabilities.PreferredVideo"/>
    /// (size and pixel format, tightly packed). One producer per camera. Returns false when no session is
    /// running or the worker takes no video.
    /// </summary>
    bool PushVideo(string cameraId, ReadOnlySpan<byte> pixels, long ptsUs);

    Task StopAsync();
}
