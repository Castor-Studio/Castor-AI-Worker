using Castor.AI.Contracts.V1;

namespace Castor.AI.Tests;

public class AiClientTests
{
    private static readonly AiCamera[] Cameras =
    [
        new("wide", "Wide", "wide"),
        new("host", "Host", "host", AudioChannel: 0),
        new("guest", "Guest", "guest", AudioChannel: 1),
    ];

    [Fact]
    public async Task Start_forwards_cameras_params_and_audio_channels_and_reports_initial_shot()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        var changes = Collect<AiOnAirChange>(h => client.OnAirChanged += h);

        await client.StartAsync(Session(AiDecisionMode.Auto, initial: "wide"));

        Assert.Equal("podcast", provider.Request!.Domain);
        Assert.Equal(["wide", "host", "guest"], provider.Request.Cameras.Select(c => c.CameraId));
        Assert.Equal(2, provider.Request.AudioChannels);
        Assert.Equal("2.0", provider.Request.Parameters["min_hold_time"]);
        Assert.Equal(new AiOnAirChange("wide", AiOnAirSource.Initial, 0), Assert.Single(changes));
        Assert.Equal("wide", client.OnAirCameraId);

        // The worker learns the initial shot as soon as it accepts the session.
        var onAir = Assert.Single(provider.OnAirMessages());
        Assert.Equal("wide", onAir.CameraId);
        Assert.False(onAir.Manual);
    }

    [Fact]
    public async Task Auto_mode_puts_accepted_suggestions_on_air()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        var suggestions = Collect<AiSuggestion>(h => client.SuggestionReceived += h);
        var changes = Collect<AiOnAirChange>(h => client.OnAirChanged += h);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        provider.Decide("host", client.NowUs);

        var suggestion = Assert.Single(suggestions);
        Assert.True(suggestion.Verdict!.Accepted);
        Assert.Equal("host", suggestion.CameraId);
        Assert.True(suggestion.LatencyMs >= 0);
        Assert.Equal(AiOnAirSource.Ai, changes[^1].Source);
        Assert.Equal("host", client.OnAirCameraId);
        Assert.Equal("host", provider.OnAirMessages().Last().CameraId);
    }

    [Fact]
    public async Task Auto_mode_rejects_stale_and_low_confidence_suggestions()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        var suggestions = Collect<AiSuggestion>(h => client.SuggestionReceived += h);
        await client.StartAsync(Session(AiDecisionMode.Auto) with { Arbiter = new ArbiterOptions { MaxDecisionAgeMs = 500, MinConfidence = 0.5f } });
        await Task.Delay(700);

        provider.Decide("host", sourcePtsUs: 0);
        provider.Decide("guest", client.NowUs, confidence: 0.2f);

        Assert.All(suggestions, s => Assert.False(s.Verdict!.Accepted));
        Assert.StartsWith("Stale", suggestions[0].Verdict!.Reason);
        Assert.StartsWith("Confidence", suggestions[1].Verdict!.Reason);
        Assert.Equal("wide", client.OnAirCameraId);
    }

    [Fact]
    public async Task Suggest_mode_never_changes_what_is_on_air()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        var suggestions = Collect<AiSuggestion>(h => client.SuggestionReceived += h);
        var changes = Collect<AiOnAirChange>(h => client.OnAirChanged += h);
        await client.StartAsync(Session(AiDecisionMode.Suggest));

        provider.Decide("host", client.NowUs);

        Assert.Null(Assert.Single(suggestions).Verdict);
        Assert.Single(changes); // initial only
        Assert.Equal("wide", client.OnAirCameraId);
    }

    [Fact]
    public async Task Report_on_air_tells_the_worker_and_locks_ai_cuts()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        var suggestions = Collect<AiSuggestion>(h => client.SuggestionReceived += h);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        client.ReportOnAir("guest");
        provider.Decide("host", client.NowUs);

        var manual = provider.OnAirMessages().Last();
        Assert.Equal("guest", manual.CameraId);
        Assert.True(manual.Manual);
        Assert.StartsWith("Manual lock", Assert.Single(suggestions).Verdict!.Reason);
        Assert.Equal("guest", client.OnAirCameraId);
    }

    [Fact]
    public async Task Report_on_air_ignores_unknown_cameras()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        client.ReportOnAir("nope");

        Assert.Equal("wide", client.OnAirCameraId);
        Assert.Single(provider.OnAirMessages());
    }

    [Fact]
    public async Task Worker_restart_is_resynced_with_the_current_shot()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(Session(AiDecisionMode.Auto));
        client.ReportOnAir("guest");

        provider.Restart();

        var resync = provider.OnAirMessages().Last();
        Assert.Equal("guest", resync.CameraId);
        Assert.Equal("fake-2", client.Capabilities!.WorkerId);
    }

    [Fact]
    public async Task Send_payload_builds_struct_with_camera_id()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        Assert.True(client.SendPayload("camera.metadata", "host",
            new Dictionary<string, object?> { ["is_speaking"] = true, ["volume_db"] = -12.5, ["face_detected"] = null }));

        var payload = provider.Sent.Single(m => m.BodyCase == ClientMessage.BodyOneofCase.Payload).Payload;
        Assert.Equal("camera.metadata", payload.Type);
        Assert.Equal("host", payload.Data.Fields["camera_id"].StringValue);
        Assert.True(payload.Data.Fields["is_speaking"].BoolValue);
        Assert.Equal(-12.5, payload.Data.Fields["volume_db"].NumberValue);
        Assert.Equal(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue, payload.Data.Fields["face_detected"].KindCase);
    }

    [Fact]
    public async Task Events_go_through_dispatch()
    {
        var dispatched = 0;
        var provider = new FakeProvider();
        await using var client = new AiClient(provider, new AiClientOptions { Dispatch = action => { dispatched++; action(); } });
        client.OnAirChanged += (_, _) => { };
        client.StateChanged += (_, _) => { };

        await client.StartAsync(Session(AiDecisionMode.Auto));

        Assert.True(dispatched >= 2);
    }

    [Fact]
    public async Task Audio_written_per_mic_reaches_the_provider_as_paced_blocks()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        client.WriteAudio(0, new float[48_000], sampleRate: 48_000);
        await Task.Delay(300);

        Assert.InRange(provider.AudioFrames, 2 * 512, 16_000);
    }

    [Theory]
    [InlineData("", "a")]
    [InlineData("a", "a")]
    public async Task Invalid_camera_ids_are_rejected(string first, string second)
    {
        await using var client = new AiClient(new FakeProvider());
        var session = new AiSessionOptions { Domain = "podcast", Cameras = [new(first, "1"), new(second, "2")] };

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(session));
    }

    [Fact]
    public async Task Unknown_initial_camera_is_rejected()
    {
        await using var client = new AiClient(new FakeProvider());

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(Session(AiDecisionMode.Auto, initial: "nope")));
    }

    [Fact]
    public async Task Second_start_requires_stop_and_restart_works()
    {
        var provider = new FakeProvider();
        await using var client = new AiClient(provider);
        await client.StartAsync(Session(AiDecisionMode.Auto));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(Session(AiDecisionMode.Auto)));
        await client.RestartAsync(Session(AiDecisionMode.Auto) with { Cameras = Cameras[..2] });

        Assert.Equal(2, provider.Request!.Cameras.Count);
        Assert.True(client.IsRunning);
        await client.StopAsync();
        Assert.False(client.IsRunning);
        Assert.False(provider.Running);
    }

    private static AiSessionOptions Session(AiDecisionMode mode, string? initial = null) => new()
    {
        Domain = "podcast",
        Cameras = Cameras,
        Parameters = new Dictionary<string, string> { ["min_hold_time"] = "2.0" },
        Mode = mode,
        InitialCameraId = initial,
    };

    private static List<T> Collect<T>(Action<EventHandler<T>> subscribe)
    {
        var items = new List<T>();
        subscribe((_, item) =>
        {
            lock (items)
            {
                items.Add(item);
            }
        });
        return items;
    }
}
