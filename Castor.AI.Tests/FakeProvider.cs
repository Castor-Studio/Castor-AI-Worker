using Castor.AI.Contracts.V1;
using Castor.AI.Providers;

namespace Castor.AI.Tests;

/// <summary>In-memory provider: records what the client sends, lets tests play the worker.</summary>
internal sealed class FakeProvider : IAiProvider
{
    public List<ClientMessage> Sent { get; } = [];

    public AiProviderRequest? Request { get; private set; }

    public int AudioFrames { get; private set; }

    public bool Running { get; private set; }

    public string Name => "fake";

    public event Action<AiConnectionState, string>? StateChanged;

    public event Action<WorkerCapabilities>? SessionStarted;

    public event Action<WorkerMessage>? MessageReceived;

    public event Action<string>? Log;

    public Task StartAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        Running = true;
        StateChanged?.Invoke(AiConnectionState.Running, "fake running");
        SessionStarted?.Invoke(Capabilities ?? new WorkerCapabilities { WorkerId = "fake-1", Domain = request.Domain });
        return Task.CompletedTask;
    }

    public bool Send(ClientMessage message)
    {
        if (!Running)
        {
            return false;
        }

        lock (Sent)
        {
            Sent.Add(message);
        }

        return true;
    }

    public bool PushAudio(ReadOnlySpan<float> interleaved, long ptsUs)
    {
        AudioFrames += interleaved.Length / Math.Max(1, Request?.AudioChannels ?? 1);
        return Running;
    }

    public Dictionary<string, int> VideoFrames { get; } = [];

    /// <summary>Last frame pushed per camera.</summary>
    public Dictionary<string, byte[]> LastVideo { get; } = [];

    public bool PushVideo(string cameraId, ReadOnlySpan<byte> pixels, long ptsUs)
    {
        lock (VideoFrames)
        {
            VideoFrames[cameraId] = VideoFrames.GetValueOrDefault(cameraId) + 1;
            LastVideo[cameraId] = pixels.ToArray();
        }

        return Running;
    }

    /// <summary>Capabilities announced by <see cref="StartAsync"/>.</summary>
    public WorkerCapabilities? Capabilities { get; set; }

    public Task StopAsync()
    {
        Running = false;
        StateChanged?.Invoke(AiConnectionState.Stopped, "fake stopped");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Simulates a crash + automatic restart of the worker.</summary>
    public void Restart() => SessionStarted?.Invoke(new WorkerCapabilities { WorkerId = "fake-2" });

    public void Decide(string cameraId, long sourcePtsUs, float confidence = 0.9f, uint minHoldMs = 0) =>
        MessageReceived?.Invoke(new WorkerMessage
        {
            Decision = new SceneDecision
            {
                DecisionId = Guid.NewGuid().ToString("N")[..8],
                CameraId = cameraId,
                Confidence = confidence,
                SourcePtsUs = sourcePtsUs,
                MinHoldMs = minHoldMs,
                Reason = "test",
                Scores = { new CameraScore { CameraId = cameraId, Score = 1 } },
            },
        });

    public IEnumerable<CameraOnAir> OnAirMessages()
    {
        lock (Sent)
        {
            return Sent.Where(m => m.BodyCase == ClientMessage.BodyOneofCase.OnAir).Select(m => m.OnAir).ToList();
        }
    }

    public void RaiseLog(string line) => Log?.Invoke(line);
}
