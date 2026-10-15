using Castor.AI.Worker.Strategies.Podcast;
using static Castor.AI.Worker.Tests.Podcast.DirectorHarness;

namespace Castor.AI.Worker.Tests.Podcast;

/// <summary>
/// Ported from Castor-AI tests/test_podcast_ddt.py, test_podcast_director.py and
/// test_podcast.py (dev, podcast director engine v2). Same inputs, same expectations.
/// </summary>
public class PodcastDirectorTests
{
    private static readonly (string, string)[] ThreeCameras =
    [
        ("s1_host", "Cam Hote"),
        ("s2_guest", "Cam Invite"),
        ("s3_wide", "Plan Large"),
    ];

    private static readonly (string, string)[] FiveCameras =
    [
        ("s1_host", "Cam Hote"),
        ("s2_guest", "Cam Invite"),
        ("s3_wide", "Plan Large"),
        ("s1_host_zoom", "Zoom Hote"),
        ("s2_guest_zoom", "Zoom Invite"),
    ];

    // --- test_podcast_ddt.py: DDT 1, standard audio flow and mic bleed ---

    [Theory]
    [InlineData(true, -15.0, false, -60.0, "s1_host")] // only host speaks
    [InlineData(false, -60.0, true, -15.0, "s2_guest")] // only guest speaks
    [InlineData(true, -16.0, true, -28.0, "s1_host")] // guest mic picks host bleed
    [InlineData(true, -26.0, true, -14.0, "s2_guest")] // host mic picks guest bleed
    public void Audio_switching_backward_compatibility(
        bool hostSpeaking, double hostDb, bool guestSpeaking, double guestDb, string expected)
    {
        var director = new DirectorHarness(
            new PodcastOptions { MinHoldTime = 1.0, MinSpeechConfirmMs = 50, CrossGatingThresholdDb = 5.0 },
            ThreeCameras);
        var host = ("s1_host", new CameraMetadata(hostSpeaking, hostDb));
        var guest = ("s2_guest", new CameraMetadata(guestSpeaking, guestDb));

        director.Tick(0.0, host, guest);
        Assert.Equal(expected, director.Tick(0.1, host, guest));
    }

    // --- DDT 2, visual presence gating (empty chair veto) ---

    [Theory]
    [InlineData(true, null, -16.0, true)] // face present
    [InlineData(null, null, -16.0, true)] // no visual metadata: audio-only fallback
    [InlineData(false, null, -16.0, false)] // empty chair: vetoed
    [InlineData(false, null, -14.0, false)] // "0"
    [InlineData(null, false, -15.0, false)] // person absent: vetoed
    [InlineData(false, true, -16.0, true)] // profile view: face lost, person present
    public void Visual_presence_gating(bool? guestFace, bool? guestPerson, double guestDb, bool switchAllowed)
    {
        var director = new DirectorHarness(new PodcastOptions { MinHoldTime = 1.0, MinSpeechConfirmMs = 50 }, ThreeCameras);
        var hostTalking = ("s1_host", Talking(-15.0));
        var hostSilent = ("s1_host", Silent(-55.0));
        var guestSilent = ("s2_guest", Silent(-60.0));
        var guestTalking = ("s2_guest", new CameraMetadata(true, guestDb, guestFace, guestPerson));

        director.Tick(0.0, hostTalking, guestSilent);
        Assert.Equal("s1_host", director.Tick(0.1, hostTalking, guestSilent));

        director.Tick(1.5, hostSilent, guestTalking);
        var decision = director.Tick(1.6, hostSilent, guestTalking);

        if (switchAllowed)
        {
            Assert.Equal("s2_guest", decision);
        }
        else
        {
            Assert.NotEqual("s2_guest", decision);
        }
    }

    // --- DDT 3, anti ping-pong governor ---

    [Theory]
    [InlineData(true, "s3_wide")]
    [InlineData(false, "s2_guest")]
    public void Anti_ping_pong_governor(bool enabled, string expectedTurn4)
    {
        var director = new DirectorHarness(
            new PodcastOptions
            {
                MinHoldTime = 0.8,
                MinSpeechConfirmMs = 50,
                PingPongThreshold = enabled ? 3 : 0,
                PingPongWindowSec = 6.0,
            },
            ThreeCameras);
        var hostTalk = ("s1_host", Talking(-16.0));
        var hostSilent = ("s1_host", Silent(-55.0));
        var guestTalk = ("s2_guest", Talking(-16.0));
        var guestSilent = ("s2_guest", Silent(-55.0));

        director.Tick(0.0, hostTalk, guestSilent);
        Assert.Equal("s1_host", director.Tick(0.1, hostTalk, guestSilent));

        director.Tick(1.0, hostSilent, guestTalk);
        Assert.Equal("s2_guest", director.Tick(1.1, hostSilent, guestTalk));

        director.Tick(2.0, hostTalk, guestSilent);
        Assert.Equal("s1_host", director.Tick(2.1, hostTalk, guestSilent));

        director.Tick(3.0, hostSilent, guestTalk);
        Assert.Equal(expectedTurn4, director.Tick(3.1, hostSilent, guestTalk));
    }

    // --- test_podcast_director.py ---

    [Fact]
    public void Cross_gating_eliminates_mic_bleed()
    {
        var director = new DirectorHarness(
            new PodcastOptions { MinHoldTime = 2.0, MinSpeechConfirmMs = 100, CrossGatingThresholdDb = 6.0 },
            FiveCameras);
        var host = ("s1_host", Talking(-18.0));
        var guestBleed = ("s2_guest", Talking(-28.0));

        director.Tick(0.0, host, guestBleed);
        Assert.Equal("s1_host", director.Tick(0.15, host, guestBleed));
    }

    [Fact]
    public void Cross_gating_triggers_wide_on_sustained_equal_volume_debate()
    {
        var director = new DirectorHarness(
            new PodcastOptions
            {
                MinHoldTime = 1.0,
                MinSpeechConfirmMs = 100,
                DebateConfirmMs = 500,
                CrossGatingThresholdDb = 6.0,
            },
            FiveCameras);
        var host = ("s1_host", Talking(-18.0));
        var guestSilent = ("s2_guest", Silent(-60.0));
        var guest = ("s2_guest", Talking(-19.0));

        director.Tick(0.0, host, guestSilent);
        Assert.Equal("s1_host", director.Tick(0.15, host, guestSilent));

        director.Tick(1.2, host, guest);
        Assert.Null(director.Tick(1.35, host, guest)); // debate only 0 ms old: hold host

        Assert.Equal("s3_wide", director.Tick(1.9, host, guest));
    }

    [Fact]
    public void Natural_pause_does_not_cut_to_wide_too_soon()
    {
        var director = new DirectorHarness(
            new PodcastOptions { MinHoldTime = 1.0, MinSpeechConfirmMs = 100, SilenceHoldTime = 5.0 },
            FiveCameras);
        var host = ("s1_host", Talking(-18.0));
        var hostSilent = ("s1_host", Silent(-55.0));
        var guestSilent = ("s2_guest", Silent(-60.0));

        director.Tick(0.0, host, guestSilent);
        Assert.Equal("s1_host", director.Tick(0.15, host, guestSilent));

        director.Tick(2.0, hostSilent, guestSilent);
        Assert.Null(director.Tick(4.5, hostSilent, guestSilent));
        Assert.Equal("s3_wide", director.Tick(7.2, hostSilent, guestSilent));
    }

    [Fact]
    public void Fast_turn_taking_switches_once_confirmed()
    {
        var director = new DirectorHarness(new PodcastOptions { MinHoldTime = 1.5, MinSpeechConfirmMs = 200 }, FiveCameras);
        var host = ("s1_host", Talking(-16.0));
        var hostSilent = ("s1_host", Silent(-55.0));
        var guestSilent = ("s2_guest", Silent(-60.0));
        var guest = ("s2_guest", Talking(-17.0));

        director.Tick(0.0, host, guestSilent);
        Assert.Equal("s1_host", director.Tick(0.25, host, guestSilent));

        director.Tick(2.0, hostSilent, guest);
        Assert.Null(director.Tick(2.10, hostSilent, guest));
        Assert.Equal("s2_guest", director.Tick(2.25, hostSilent, guest));
    }

    // --- test_podcast.py ---

    [Fact]
    public void State_machine_ticks_through_hold_window()
    {
        var director = new DirectorHarness(
            new PodcastOptions { MinHoldTime = 6.0, MonologueTime = 2.0, MinSpeechConfirmMs = 0 },
            ("s-host", "Cam Hote"),
            ("s-guest", "Cam Invite"),
            ("s-guest-zoom", "Cam Invite Zoom"));
        var host = ("s-host", new CameraMetadata(IsSpeaking: true));
        var guest = ("s-guest", new CameraMetadata(IsSpeaking: true));

        Assert.Equal("s-host", director.Tick(0.0, host));
        Assert.Null(director.Tick(0.5, guest));
        Assert.Null(director.Tick(3.0, guest));

        // Guest talking since 0.5 s: past monologue_time when the hold expires, so zoom directly.
        Assert.Equal("s-guest-zoom", director.Tick(6.5, guest));
    }

    // --- test_podcast_sdd.py (podcast-engine-optimizations), scenario 1 ---

    [Fact]
    public void Scenario_live_debate_with_monologue_zoom_and_ping_pong_recovery()
    {
        var director = new DirectorHarness(
            new PodcastOptions
            {
                MinHoldTime = 1.0,
                MonologueTime = 4.0,
                MinSpeechConfirmMs = 50,
                DebateConfirmMs = 100,
                PingPongThreshold = 4,
                PingPongWindowSec = 6.0,
                CrossGatingThresholdDb = 5.0,
            },
            ("s1_host", "Cam Hote"),
            ("s1_host_zoom", "Zoom Hote"),
            ("s2_guest", "Cam Invite"),
            ("s3_wide", "Plan Large"));
        (string, CameraMetadata)[] hostTalks =
        [
            ("s1_host", new CameraMetadata(true, -14.0, PersonDetected: true)),
            ("s2_guest", new CameraMetadata(false, -55.0, PersonDetected: true)),
        ];
        (string, CameraMetadata)[] guestTalks =
        [
            ("s1_host", new CameraMetadata(false, -50.0, PersonDetected: true)),
            ("s2_guest", new CameraMetadata(true, -15.0, PersonDetected: true)),
        ];

        // Host opens and keeps talking: cut, then monologue zoom.
        director.Tick(0.0, hostTalks);
        Assert.Equal("s1_host", director.Tick(0.1, hostTalks));
        Assert.Equal("s1_host_zoom", director.Tick(4.5, hostTalks));

        // Guest answers.
        director.Tick(6.0, guestTalks);
        Assert.Equal("s2_guest", director.Tick(6.1, guestTalks));

        // Rapid back and forth.
        director.Tick(7.3, hostTalks);
        Assert.Equal("s1_host", director.Tick(7.4, hostTalks));
        director.Tick(8.5, guestTalks);
        Assert.Equal("s2_guest", director.Tick(8.6, guestTalks));
        director.Tick(9.7, hostTalks);
        Assert.Equal("s3_wide", director.Tick(9.8, hostTalks));
    }

    // --- C# port specific: the app owns what is on air ---

    [Fact]
    public void Initial_shot_is_not_a_cut()
    {
        var director = new PodcastDirector(
            new PodcastOptions { MinHoldTime = 5.0, MinSpeechConfirmMs = 0 },
            new Dictionary<string, string> { ["host"] = "cam-host", ["wide"] = "cam-wide" },
            "cam-host");
        director.OnAir("cam-wide", 0.0);

        var tick = director.Tick(Speaking("host"), 0.1);

        Assert.Equal("cam-host", tick.DecisionCameraId);
    }

    [Fact]
    public void Rejected_suggestion_is_repeated_after_resuggest_delay()
    {
        var director = new PodcastDirector(
            new PodcastOptions { MinHoldTime = 0.0, MinSpeechConfirmMs = 0 },
            new Dictionary<string, string> { ["host"] = "cam-host", ["wide"] = "cam-wide" },
            "cam-host");
        director.OnAir("cam-wide", 0.0);

        Assert.Equal("cam-host", director.Tick(Speaking("host"), 0.1).DecisionCameraId);
        // The app did not take it (arbiter / manual lock): no spam...
        Assert.Null(director.Tick(Speaking("host"), 0.5).DecisionCameraId);
        // ...but the suggestion comes back.
        Assert.Equal("cam-host", director.Tick(Speaking("host"), 0.1 + PodcastDirector.ResuggestSeconds).DecisionCameraId);
    }

    [Fact]
    public void Manual_take_restarts_hold_window()
    {
        var director = new PodcastDirector(
            new PodcastOptions { MinHoldTime = 2.0, MinSpeechConfirmMs = 0 },
            new Dictionary<string, string> { ["host"] = "cam-host", ["guest"] = "cam-guest", ["wide"] = "cam-wide" },
            "cam-host");
        director.OnAir("cam-wide", 0.0);
        director.OnAir("cam-guest", 5.0); // operator takes the guest camera

        Assert.Null(director.Tick(Speaking("host"), 6.0).DecisionCameraId);
        Assert.Equal("cam-host", director.Tick(Speaking("host"), 7.0).DecisionCameraId);
    }

    private static Dictionary<string, SpeakerSignal> Speaking(string role) =>
        new() { [role] = new SpeakerSignal(true, -15.0, CameraMetadata.Empty) };
}
