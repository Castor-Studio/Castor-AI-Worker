using Avalonia.Media;

namespace Castor.Proto.App.Charts;

/// <summary>Shared colors: one per camera index, reused by cards, charts and the timeline.</summary>
public static class Palette
{
    private static readonly Color[] Colors =
    [
        Color.Parse("#42A5F5"), Color.Parse("#66BB6A"), Color.Parse("#FFA726"), Color.Parse("#AB47BC"),
        Color.Parse("#26C6DA"), Color.Parse("#EC407A"), Color.Parse("#D4E157"), Color.Parse("#8D6E63"),
    ];

    public static Color At(int index) => Colors[index % Colors.Length];
}

/// <summary>Values over session time (seconds). Mutated on the UI thread only.</summary>
public sealed class TimeSeries(string name, Color color)
{
    private readonly List<(double Time, double Value)> _points = [];

    public string Name { get; } = name;

    public Color Color { get; } = color;

    public IReadOnlyList<(double Time, double Value)> Points => _points;

    public double? Last => _points.Count == 0 ? null : _points[^1].Value;

    public void Add(double time, double value)
    {
        if (double.IsFinite(value))
        {
            _points.Add((time, value));
        }
    }

    public void TrimBefore(double time)
    {
        var drop = 0;
        while (drop < _points.Count - 1 && _points[drop + 1].Time < time)
        {
            drop++;
        }

        if (drop > 0)
        {
            _points.RemoveRange(0, drop);
        }
    }

    public void Clear() => _points.Clear();
}

public abstract class ScrollingModel
{
    public double WindowSeconds { get; set; } = 60;

    /// <summary>Right edge of the view, session seconds.</summary>
    public double Now { get; private set; }

    public event Action? Changed;

    public void Advance(double now)
    {
        Now = now;
        Trim(now - WindowSeconds - 1);
        Changed?.Invoke();
    }

    protected abstract void Trim(double before);
}

public sealed class ChartModel(string title, string unit, double? min = null, double? max = null) : ScrollingModel
{
    public string Title { get; } = title;

    public string Unit { get; } = unit;

    public double? Min { get; } = min;

    public double? Max { get; } = max;

    public List<TimeSeries> Series { get; } = [];

    public TimeSeries GetOrAdd(string name, Color color)
    {
        var series = Series.FirstOrDefault(s => s.Name == name);
        if (series is null)
        {
            series = new TimeSeries(name, color);
            Series.Add(series);
        }

        return series;
    }

    public void Clear()
    {
        foreach (var series in Series)
        {
            series.Clear();
        }
    }

    protected override void Trim(double before)
    {
        foreach (var series in Series)
        {
            series.TrimBefore(before);
        }
    }
}

public enum ShotSource
{
    Initial,
    Ai,
    Manual,
}

public sealed record TimelineRow(string Id, string Label, Color Color);

public sealed class TimelineSegment(string cameraId, double start, ShotSource source)
{
    public string CameraId { get; } = cameraId;

    public double Start { get; } = start;

    public double? End { get; set; }

    public ShotSource Source { get; } = source;
}

/// <param name="Accepted">Arbiter verdict; null in suggest mode (no arbiter).</param>
public sealed record TimelineMarker(double Time, string CameraId, bool? Accepted);

/// <summary>What was on air, and every AI suggestion, over the last minute.</summary>
public sealed class TimelineModel : ScrollingModel
{
    public List<TimelineRow> Rows { get; } = [];

    public List<TimelineSegment> Segments { get; } = [];

    public List<TimelineMarker> Markers { get; } = [];

    public void SetRows(IEnumerable<TimelineRow> rows)
    {
        Rows.Clear();
        Rows.AddRange(rows);
        Segments.Clear();
        Markers.Clear();
    }

    public void OnAir(string cameraId, double time, ShotSource source)
    {
        if (Segments.Count > 0 && Segments[^1].End is null)
        {
            Segments[^1].End = time;
        }

        Segments.Add(new TimelineSegment(cameraId, time, source));
    }

    public void Close(double time)
    {
        if (Segments.Count > 0 && Segments[^1].End is null)
        {
            Segments[^1].End = time;
        }
    }

    public void Mark(double time, string cameraId, bool? accepted) => Markers.Add(new TimelineMarker(time, cameraId, accepted));

    protected override void Trim(double before)
    {
        Segments.RemoveAll(s => s.End is { } end && end < before);
        Markers.RemoveAll(m => m.Time < before);
    }
}
