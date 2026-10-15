using Castor.AI.Contracts.Media;
using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Models;
using Castor.AI.Worker.Tests.Models;
using Castor.AI.Worker.Strategies.Football;

namespace Castor.AI.Worker.Tests.Football;

/// <summary>
/// Whole worker path with the real model: shared memory frames, detection, focus, decision.
/// The model is downloaded on first run, like the worker does.
/// </summary>
public sealed class FootballStrategyTests : IDisposable
{
    private static readonly Lazy<YoloBallModel> Model =
        new(() => new YoloBallModel(TestModels.Path(ModelCatalog.FootballBall)));

    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly List<VideoRingWriter> _writers = [];
    private readonly SessionStart _start;
    private readonly SessionMedia _media;
    private readonly FootballStrategy _strategy = new(Model.Value);
    private readonly byte[] _ball = Fixtures.Bgra("pitch_ball");
    private readonly byte[] _empty = Fixtures.Bgra("pitch_empty");
    private long _pts;

    public FootballStrategyTests()
    {
        _start = new SessionStart
        {
            SessionId = _sessionId,
            Domain = FootballStrategy.Domain,
            Cameras =
            {
                new CameraDescriptor { CameraId = "left", Label = "Left", AudioChannel = -1 },
                new CameraDescriptor { CameraId = "right", Label = "Right", AudioChannel = -1 },
            },
        };

        var transport = new SharedMemoryTransport();
        for (var i = 0; i < _start.Cameras.Count; i++)
        {
            var name = VideoRingLayout.RegionName(_sessionId, i);
            var writer = VideoRingWriter.Create(name, Fixtures.Width, Fixtures.Height, PixelFormat.Bgra8);
            _writers.Add(writer);
            transport.Video.Add(new VideoRegion { CameraId = _start.Cameras[i].CameraId, Name = name, SizeBytes = (ulong)writer.SizeBytes });
        }

        _media = SessionMedia.Open(new MediaTransport { SharedMemory = transport });
        _strategy.Start(_start, _media, _ => { });
        _strategy.OnAir(new CameraOnAir { CameraId = "left" });
    }

    [Fact]
    public void Cuts_to_the_camera_that_alone_sees_the_ball()
    {
        var decision = Round(_empty, _ball);

        Assert.NotNull(decision);
        Assert.Equal("right", decision.CameraId);
        Assert.Equal(1.0f, decision.Confidence); // default_confidence
        Assert.StartsWith("Ball only on Right", decision.Reason);
        Assert.Equal(_pts, decision.SourcePtsUs);
        var scores = decision.Scores.ToDictionary(s => s.CameraId, s => s.Score);
        Assert.True(scores["right"] >= 0.6f && scores["left"] < 0.25f, $"scores {decision.Scores}");
    }

    [Fact]
    public void Keeps_the_focus_when_both_or_no_camera_sees_the_ball()
    {
        Assert.Equal("right", Round(_empty, _ball)?.CameraId);
        _strategy.OnAir(new CameraOnAir { CameraId = "right" });

        Assert.Null(Round(_ball, _ball));
        Assert.Null(Round(_empty, _empty));
        Assert.Equal("left", Round(_ball, _empty)?.CameraId);
    }

    [Fact]
    public void Nothing_without_new_frames_and_stats_describe_cameras()
    {
        Round(_empty, _ball);

        Assert.Null(_strategy.Evaluate(_pts));

        var stats = new WorkerStats();
        _strategy.FillStats(stats);
        Assert.Contains("ball", stats.Cameras["right"].Flags);
        Assert.Contains("focus", stats.Cameras["right"].Flags);
        Assert.DoesNotContain("ball", stats.Cameras["left"].Flags);
        Assert.True(stats.Cameras["right"].Metrics["ball_conf"] >= 0.6);
        Assert.StartsWith("focus=Right", stats.Detail);
    }

    [Fact]
    public void Frameskip_analyses_one_round_out_of_n_plus_one()
    {
        var strategy = new FootballStrategy(Model.Value);
        var start = _start.Clone();
        start.Params["frameskip"] = "1";
        strategy.Start(start, _media, _ => { });
        strategy.OnAir(new CameraOnAir { CameraId = "left" });

        Write(_empty, _ball);
        Assert.Equal("right", strategy.Evaluate(_pts)?.CameraId); // round 0: analysed
        strategy.OnAir(new CameraOnAir { CameraId = "right" });
        Write(_ball, _empty);
        Assert.Null(strategy.Evaluate(_pts)); // round 1: skipped
        Write(_ball, _empty);
        Assert.Equal("left", strategy.Evaluate(_pts)?.CameraId); // round 2: analysed
    }

    [Fact]
    public void Session_without_video_is_refused()
    {
        using var media = SessionMedia.Open(null);
        var strategy = new FootballStrategy(Model.Value);

        var error = Assert.Throws<InvalidOperationException>(() => strategy.Start(_start, media, _ => { }));
        Assert.Contains("needs video", error.Message);
    }

    public void Dispose()
    {
        _strategy.Dispose();
        _media.Dispose();
        foreach (var writer in _writers)
        {
            writer.Dispose();
        }
    }

    private SceneDecision? Round(byte[] left, byte[] right)
    {
        Write(left, right);
        return _strategy.Evaluate(_pts);
    }

    private void Write(byte[] left, byte[] right)
    {
        _pts += 66_000;
        _writers[0].Write(left, _pts);
        _writers[1].Write(right, _pts);
    }
}
