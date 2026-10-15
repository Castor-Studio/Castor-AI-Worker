using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Strategies.Podcast;

namespace Castor.AI.Worker.Tests.Podcast;

/// <summary>
/// Drives a <see cref="PodcastDirector"/> like the Python tests drive <c>_analyze_sync</c>:
/// metadata-only sources, explicit clock, and the app applying every decision immediately.
/// </summary>
internal sealed class DirectorHarness
{
    private readonly PodcastDirector _director;

    public DirectorHarness(PodcastOptions options, params (string Id, string Label)[] cameras)
    {
        var descriptors = cameras
            .Select(c => new CameraDescriptor { CameraId = c.Id, Label = c.Label, AudioChannel = -1 })
            .ToList();
        Roles = PodcastRoles.Resolve(descriptors);
        _director = new PodcastDirector(options, Roles, cameras[0].Id);
    }

    public IReadOnlyDictionary<string, string> Roles { get; }

    public string? CurrentCameraId => _director.CurrentCameraId;

    /// <returns>Camera of the emitted decision, or null.</returns>
    public string? Tick(double now, params (string CameraId, CameraMetadata Metadata)[] metadata)
    {
        var byCamera = metadata.ToDictionary(m => m.CameraId, m => m.Metadata);
        var signals = Roles
            .Where(r => PodcastRoles.IsSpeakerRole(r.Key))
            .ToDictionary(
                r => r.Key,
                r => new SpeakerSignal(false, -100.0, byCamera.GetValueOrDefault(r.Value, CameraMetadata.Empty)));

        var tick = _director.Tick(signals, now);
        if (tick.DecisionCameraId is { } camera)
        {
            _director.OnAir(camera, now);
        }

        return tick.DecisionCameraId;
    }

    public static CameraMetadata Talking(double volumeDb) => new(IsSpeaking: true, VolumeDb: volumeDb);

    public static CameraMetadata Silent(double volumeDb) => new(IsSpeaking: false, VolumeDb: volumeDb);
}
