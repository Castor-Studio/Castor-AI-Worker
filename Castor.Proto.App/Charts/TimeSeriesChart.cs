using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Castor.Proto.App.Charts;

/// <summary>Scrolling line chart of a <see cref="ChartModel"/> (last N seconds, newest on the right).</summary>
public sealed class TimeSeriesChart : Control
{
    public static readonly StyledProperty<ChartModel?> ModelProperty =
        AvaloniaProperty.Register<TimeSeriesChart, ChartModel?>(nameof(Model));

    private const double LeftMargin = 44;
    private const double TopMargin = 40;
    private const double BottomMargin = 16;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#10FFFFFF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#B0FFFFFF"));
    private static readonly IBrush TitleBrush = Brushes.White;
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#20FFFFFF")), 1);
    private static readonly Typeface Font = new(FontFamily.Default);

    static TimeSeriesChart() => AffectsRender<TimeSeriesChart>(ModelProperty);

    public ChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (change.OldValue is ChartModel old)
            {
                old.Changed -= InvalidateVisual;
            }

            if (change.NewValue is ChartModel model)
            {
                model.Changed += InvalidateVisual;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Model is { } model)
        {
            model.Changed -= InvalidateVisual;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Model is { } model)
        {
            model.Changed -= InvalidateVisual;
            model.Changed += InvalidateVisual;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds, 4);
        if (Model is not { } model)
        {
            return;
        }

        DrawText(context, Unit(model.Title, model.Unit), new Point(6, 2), 12, TitleBrush);

        // Legend with last values.
        var x = 6.0;
        foreach (var series in model.Series)
        {
            var text = $"{series.Name} {Format(series.Last)}";
            var formatted = Text(text, 11, new SolidColorBrush(series.Color));
            context.DrawText(formatted, new Point(x, 18));
            x += formatted.Width + 12;
        }

        var plot = new Rect(LeftMargin, TopMargin, Math.Max(1, bounds.Width - LeftMargin - 6), Math.Max(1, bounds.Height - TopMargin - BottomMargin));
        var start = model.Now - model.WindowSeconds;
        var (min, max) = Range(model, start);

        for (var i = 0; i <= 2; i++)
        {
            var value = min + (max - min) * i / 2;
            var y = plot.Bottom - plot.Height * i / 2;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, Format(value), new Point(4, y - 7), 10, TextBrush);
        }

        DrawText(context, $"-{model.WindowSeconds:0}s", new Point(plot.Left, plot.Bottom + 1), 10, TextBrush);
        DrawText(context, "now", new Point(plot.Right - 22, plot.Bottom + 1), 10, TextBrush);

        using (context.PushClip(plot))
        {
            foreach (var series in model.Series)
            {
                DrawSeries(context, series, plot, start, model.WindowSeconds, min, max);
            }
        }
    }

    private static void DrawSeries(DrawingContext context, TimeSeries series, Rect plot, double start, double window, double min, double max)
    {
        if (series.Points.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = true;
            foreach (var (time, value) in series.Points)
            {
                var point = new Point(
                    plot.Left + (time - start) / window * plot.Width,
                    plot.Bottom - (Math.Clamp(value, min, max) - min) / (max - min) * plot.Height);
                if (first)
                {
                    g.BeginFigure(point, false);
                    first = false;
                }
                else
                {
                    g.LineTo(point);
                }
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Color), 1.5), geometry);

        if (series.Points.Count == 1)
        {
            var (time, value) = series.Points[0];
            var center = new Point(plot.Left + (time - start) / window * plot.Width,
                plot.Bottom - (Math.Clamp(value, min, max) - min) / (max - min) * plot.Height);
            context.DrawEllipse(new SolidColorBrush(series.Color), null, center, 2, 2);
        }
    }

    private static (double Min, double Max) Range(ChartModel model, double start)
    {
        if (model.Min is { } fixedMin && model.Max is { } fixedMax && fixedMax > fixedMin)
        {
            return (fixedMin, fixedMax);
        }

        var values = model.Series.SelectMany(s => s.Points).Where(p => p.Time >= start).Select(p => p.Value).ToList();
        var min = model.Min ?? (values.Count > 0 ? values.Min() : 0);
        var max = model.Max ?? (values.Count > 0 ? values.Max() : 1);
        if (max - min < 1e-9)
        {
            max = min + 1;
        }

        // A bit of headroom for auto ranges.
        if (model.Max is null)
        {
            max += (max - min) * 0.1;
        }

        return (min, max);
    }

    private static string Unit(string title, string unit) => string.IsNullOrEmpty(unit) ? title : $"{title} ({unit})";

    private static string Format(double? value) => value switch
    {
        null => "-",
        var v when Math.Abs(v.Value) >= 100 => v.Value.ToString("0", CultureInfo.InvariantCulture),
        var v when Math.Abs(v.Value) >= 10 => v.Value.ToString("0.0", CultureInfo.InvariantCulture),
        var v => v.Value.ToString("0.00", CultureInfo.InvariantCulture),
    };

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);

    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush) =>
        context.DrawText(Text(text, size, brush), origin);
}
