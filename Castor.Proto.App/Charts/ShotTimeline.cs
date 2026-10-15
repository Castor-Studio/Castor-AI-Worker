using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Castor.Proto.App.Charts;

/// <summary>
/// One row per camera: on-air segments (AI / manual / initial) and AI suggestions
/// (filled marker = accepted by the arbiter, hollow = rejected), last N seconds.
/// </summary>
public sealed class ShotTimeline : Control
{
    public static readonly StyledProperty<TimelineModel?> ModelProperty =
        AvaloniaProperty.Register<ShotTimeline, TimelineModel?>(nameof(Model));

    private const double LabelWidth = 90;
    private const double AxisHeight = 16;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#10FFFFFF"));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#08FFFFFF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#C0FFFFFF"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#18FFFFFF")), 1);
    private static readonly IPen RejectedPen = new Pen(new SolidColorBrush(Color.Parse("#E57373")), 1.5);
    private static readonly IPen SuggestedPen = new Pen(new SolidColorBrush(Color.Parse("#FFCA28")), 1.5);
    private static readonly IBrush AcceptedBrush = new SolidColorBrush(Color.Parse("#81C784"));
    private static readonly Typeface Font = new(FontFamily.Default);

    static ShotTimeline() => AffectsRender<ShotTimeline>(ModelProperty);

    public TimelineModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            if (change.OldValue is TimelineModel old)
            {
                old.Changed -= InvalidateVisual;
            }

            if (change.NewValue is TimelineModel model)
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
        if (Model is not { } model || model.Rows.Count == 0)
        {
            return;
        }

        var plot = new Rect(LabelWidth, 4, Math.Max(1, bounds.Width - LabelWidth - 8), Math.Max(1, bounds.Height - AxisHeight - 4));
        var rowHeight = plot.Height / model.Rows.Count;
        var start = model.Now - model.WindowSeconds;
        double X(double time) => plot.Left + (time - start) / model.WindowSeconds * plot.Width;

        var rowIndex = new Dictionary<string, int>();
        for (var i = 0; i < model.Rows.Count; i++)
        {
            var row = model.Rows[i];
            rowIndex[row.Id] = i;
            var rowRect = new Rect(plot.Left, plot.Top + i * rowHeight + 1, plot.Width, rowHeight - 2);
            context.FillRectangle(RowBrush, rowRect);
            context.FillRectangle(new SolidColorBrush(row.Color), new Rect(6, rowRect.Top + rowHeight / 2 - 4, 8, 8), 2);
            context.DrawText(Text(row.Label, 12, TextBrush), new Point(18, rowRect.Top + rowHeight / 2 - 8));
        }

        // Time grid every 10 s.
        for (var t = Math.Ceiling(start / 10) * 10; t <= model.Now; t += 10)
        {
            var x = X(t);
            context.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawText(Text($"{t:0}s", 10, TextBrush), new Point(x - 8, plot.Bottom + 1));
        }

        using (context.PushClip(plot))
        {
            foreach (var segment in model.Segments)
            {
                if (!rowIndex.TryGetValue(segment.CameraId, out var i))
                {
                    continue;
                }

                var color = model.Rows[i].Color;
                var left = X(segment.Start);
                var right = X(segment.End ?? model.Now);
                var rect = new Rect(left, plot.Top + i * rowHeight + rowHeight * 0.2, Math.Max(1, right - left), rowHeight * 0.6);
                var fill = segment.Source switch
                {
                    ShotSource.Ai => new SolidColorBrush(color),
                    ShotSource.Manual => new SolidColorBrush(color, 0.45),
                    _ => new SolidColorBrush(color, 0.25),
                };
                context.FillRectangle(fill, rect, 2);
                if (segment.Source == ShotSource.Manual)
                {
                    context.DrawRectangle(new Pen(new SolidColorBrush(color), 1, DashStyle.Dash), rect, 2);
                }
            }

            foreach (var marker in model.Markers)
            {
                if (!rowIndex.TryGetValue(marker.CameraId, out var i))
                {
                    continue;
                }

                var center = new Point(X(marker.Time), plot.Top + i * rowHeight + rowHeight * 0.2);
                var triangle = Triangle(center, 5);
                switch (marker.Accepted)
                {
                    case true:
                        context.DrawGeometry(AcceptedBrush, null, triangle);
                        break;
                    case false:
                        context.DrawGeometry(null, RejectedPen, triangle);
                        break;
                    default:
                        context.DrawGeometry(null, SuggestedPen, triangle);
                        break;
                }
            }
        }
    }

    private static StreamGeometry Triangle(Point tip, double size)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(tip.X - size, tip.Y - size * 1.4), true);
        g.LineTo(new Point(tip.X + size, tip.Y - size * 1.4));
        g.LineTo(tip);
        g.EndFigure(true);
        return geometry;
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
}
