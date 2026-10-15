namespace Castor.AI.Worker.Strategies.Football;

/// <summary>
/// Port of <c>FootballAnalyzer.analyze_once</c> + <c>FootballModule.analyze_sources</c> (Castor-AI):
/// <list type="bullet">
/// <item>the ball on exactly one camera: focus that camera;</item>
/// <item>the ball on several cameras or on none: keep the previous focus;</item>
/// <item>no decision until every camera has been analysed once (Python waits for both streams).</item>
/// </list>
/// Python handles two streams; the same rule is applied to N cameras (for 2 it is identical).
/// Differences with Python: the current shot comes from the app (<see cref="OnAir"/>) instead of the
/// last sent decision, and a refused decision is suggested again after <see cref="ResuggestSeconds"/>
/// (same as the podcast director, docs/architecture.md section 7).
/// </summary>
public sealed class FootballFocus(IReadOnlyList<string> cameraIds)
{
    public const double ResuggestSeconds = 1.0;

    private readonly Dictionary<string, bool> _found = [];
    private string? _onAir;
    private string? _lastEmitted;
    private double _lastEmittedAt;

    /// <summary>Python <c>last_focus</c>. Null until the ball has been seen on a single camera.</summary>
    public string? Focus { get; private set; }

    /// <summary>Why the focus is what it is (debug).</summary>
    public string Reason { get; private set; } = "waiting for every camera";

    public string? OnAirCameraId => _onAir;

    public void OnAir(string cameraId) => _onAir = cameraId;

    /// <summary>Latest detection of one camera.</summary>
    public void Report(string cameraId, bool ballFound) => _found[cameraId] = ballFound;

    /// <summary>Applies the rule to the latest detections, returns the camera to suggest or null.</summary>
    public string? Decide(double now)
    {
        if (cameraIds.Any(id => !_found.ContainsKey(id)))
        {
            Reason = "waiting for every camera";
            return null;
        }

        var withBall = cameraIds.Where(id => _found[id]).ToList();
        if (withBall.Count == 1)
        {
            Focus = withBall[0];
            Reason = "ball on this camera only";
        }
        else
        {
            Reason = withBall.Count == 0
                ? "ball on no camera, keeping previous focus"
                : "ball on several cameras, keeping previous focus";
        }

        if (Focus is null || Focus == _onAir)
        {
            return null;
        }

        if (Focus == _lastEmitted && now - _lastEmittedAt < ResuggestSeconds)
        {
            return null;
        }

        _lastEmitted = Focus;
        _lastEmittedAt = now;
        return Focus;
    }
}
