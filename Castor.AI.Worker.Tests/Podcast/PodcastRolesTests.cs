using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Strategies.Podcast;

namespace Castor.AI.Worker.Tests.Podcast;

public class PodcastRolesTests
{
    [Theory]
    [InlineData("Cam Hote", "host")]
    [InlineData("Présentateur", null)] // accent not in the Python keyword list either
    [InlineData("Presentateur", "host")]
    [InlineData("Cam Invite", "guest")]
    [InlineData("Guest 1", "guest")]
    [InlineData("Invité 2", "guest2")]
    [InlineData("Panéliste 3", "guest3")]
    [InlineData("Cam 3", "guest2")] // bare camN is offset by the host's cam1
    [InlineData("Plan Large", null)]
    public void Classifies_labels_like_python(string label, string? expected) =>
        Assert.Equal(expected, PodcastRoles.ClassifySpeakerLabel(label));

    [Fact]
    public void Explicit_roles_win_over_labels()
    {
        var roles = PodcastRoles.Resolve(
        [
            Camera("a", "Plan Large", "host"),
            Camera("b", "whatever", "wide"),
            Camera("c", "Cam Hote", "guest1"),
            Camera("d", "x", "guest2"),
        ]);

        Assert.Equal("a", roles["host"]);
        Assert.Equal("b", roles["wide"]);
        Assert.Equal("c", roles["guest"]);
        Assert.Equal("d", roles["guest2"]);
    }

    [Fact]
    public void Unknown_roles_fall_back_to_label_heuristics()
    {
        var roles = PodcastRoles.Resolve(
        [
            Camera("cam-1", "Cam Hote", "closeup"),
            Camera("cam-2", "Zoom Invite", ""),
            Camera("cam-3", "Plan Large", ""),
            Camera("cam-4", "Cam Invite", ""),
        ]);

        Assert.Equal("cam-1", roles["host"]);
        Assert.Equal("cam-2", roles["guest_zoom"]);
        Assert.Equal("cam-3", roles["wide"]);
        Assert.Equal("cam-4", roles["guest"]);
    }

    [Fact]
    public void Third_unlabeled_camera_is_wide_in_three_camera_rig()
    {
        var roles = PodcastRoles.Resolve([Camera("a", "A", ""), Camera("b", "B", ""), Camera("c", "C", "")]);

        Assert.Equal("a", roles["host"]);
        Assert.Equal("b", roles["guest"]);
        Assert.Equal("c", roles["wide"]);
    }

    [Fact]
    public void Extra_unlabeled_cameras_become_panelists()
    {
        var roles = PodcastRoles.Resolve(
        [
            Camera("w", "Studio", ""),
            Camera("a", "A", ""),
            Camera("b", "B", ""),
            Camera("c", "C", ""),
        ]);

        Assert.Equal("w", roles["wide"]);
        Assert.Equal("a", roles["host"]);
        Assert.Equal("b", roles["guest"]);
        Assert.Equal("c", roles["guest2"]);
    }

    private static CameraDescriptor Camera(string id, string label, string role) =>
        new() { CameraId = id, Label = label, Role = role, AudioChannel = -1 };
}
