using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Castor.AI.Dashboard.Video;

/// <summary>Two views can share a preview bitmap and both repaint when its pixels change.</summary>
public sealed class VideoPreviewControl : Control
{
    private bool _isAttached;

    public static readonly StyledProperty<VideoPreview?> PreviewProperty =
        AvaloniaProperty.Register<VideoPreviewControl, VideoPreview?>(nameof(Preview));

    public VideoPreview? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PreviewProperty) return;
        if (change.OldValue is VideoPreview old) old.Changed -= InvalidateVisual;
        if (_isAttached && change.NewValue is VideoPreview current)
            current.Changed += InvalidateVisual;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        if (Preview is { } preview)
        {
            preview.Changed -= InvalidateVisual;
            preview.Changed += InvalidateVisual;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (Preview is { } preview) preview.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Preview?.Bitmap is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = bitmap.Size;
        var scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
        var width = size.Width * scale;
        var height = size.Height * scale;
        context.DrawImage(bitmap, new Rect(size),
            new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height));
    }
}
