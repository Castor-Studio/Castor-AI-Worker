using Avalonia.Media;

namespace Castor.Proto.App.ViewModels;

public sealed record DecisionItem(
    string Time,
    string Camera,
    IBrush CameraColor,
    string Confidence,
    string Latency,
    string Processing,
    bool Accepted,
    string Verdict,
    string Reason,
    string ArbiterReason,
    string Details);

public sealed record OnAirItem(string Time, string Camera, IBrush CameraColor, string Source);

public sealed record LogItem(string Time, string Source, string Text);

/// <summary>Rolling decision latency percentiles (receipt time minus decision source pts).</summary>
public sealed class LatencyWindow(double windowSeconds)
{
    private readonly List<(double Time, double AgeMs)> _samples = [];

    public void Add(double time, double ageMs)
    {
        _samples.Add((time, ageMs));
        _samples.RemoveAll(s => s.Time < time - windowSeconds);
    }

    public void Clear() => _samples.Clear();

    public string Summary()
    {
        if (_samples.Count == 0)
        {
            return "no decision yet";
        }

        var sorted = _samples.Select(s => s.AgeMs).Order().ToList();
        double Percentile(double p) => sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        return $"last {windowSeconds:0}s, {sorted.Count} decisions: p50 {Percentile(0.5):0.0} ms · p95 {Percentile(0.95):0.0} ms · max {sorted[^1]:0.0} ms";
    }
}
