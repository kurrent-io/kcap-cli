using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using MarkView.Avalonia;

namespace Capacitor.App.Views;

/// An image: its label until the picture arrives, the picture after, the label for good if it
/// never does. A press opens `Target` through the view's link command when set; otherwise the
/// image is display-only.
public sealed class MarkdownImage : Panel {
    /// A paragraph's line height is exact, so an inline picture may be no taller than fits one
    /// line; a picture on a line of its own is a block and takes its natural size.
    public const double InlineHeight = 18;
    const double InlineHang = 6;

    readonly TextBlock _label;
    readonly Image _picture;
    readonly ImageSize? _size;
    bool _pressed;

    public MarkdownImage(string url, string label, ImageSize? size, string? target, bool inline) {
        Url = url;
        Target = target;
        IsInline = inline;
        _size = size;
        _label = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _label.Classes.Add("markdown-image-label");
        // Not MarkView's `markdown-image` class: its theme margin would grow an inline past the line.
        _picture = new Image { IsVisible = false, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        if (size?.Width is { } width) _picture.Width = width;
        if (size?.Height is { } height) _picture.Height = height;
        if (inline) {
            _picture.MaxHeight = InlineHeight;
            _label.MaxHeight = InlineHeight;
            _label.TextWrapping = TextWrapping.NoWrap;
            // The line takes an embedded control's whole height as its ascent, which would push
            // the text down. Measured 6px shorter, the control hangs that far below the baseline
            // instead, into the descent the line already has.
            Margin = new Thickness(0, 0, 0, -InlineHang);
        }
        Children.Add(_label);
        Children.Add(_picture);
        // A transparent fill makes the whole box hit-testable, not just the glyphs or pixels.
        Background = Brushes.Transparent;
        if (target is not null) Cursor = new Cursor(StandardCursorType.Hand);
    }

    public string Url { get; }

    public string? Target { get; }

    public bool IsInline { get; }

    public string Label => _label.Text ?? "";

    public bool ShowsPicture => _picture.IsVisible;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
        _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    async Task LoadAsync() {
        var image = await MarkdownImages.LoadAsync(Url);
        if (image is null || VisualRoot is null) return;
        _picture.Source = image;
        _picture.IsVisible = true;
        _label.IsVisible = false;
    }

    /// Caps the picture to the pane: measured from the left of the row or text block holding it
    /// against `MarkdownView` (the scroll pane's child), not the viewer — the viewer can already
    /// have grown with an unconstrained picture. A width the tag asked for that exceeds the pane
    /// is dropped so Uniform can shrink with aspect; a height-only tag (badges) keeps its height.
    void OnLayoutUpdated(object? sender, EventArgs e) {
        if (!_picture.IsVisible) return;
        var pane = (Control?)this.FindAncestorOfType<MarkdownView>() ?? this.FindAncestorOfType<MarkdownViewer>();
        if (pane is null || pane.Bounds.Width <= 0) return;
        var host = this.FindAncestorOfType<TextBlock>() ?? this.GetVisualParent() as Visual ?? this;
        if (host.TranslatePoint(new Point(0, 0), pane) is not { } origin) return;
        var available = pane.Bounds.Width - origin.X - 2;
        if (available <= 0) return;
        if (_picture.Width > available) {
            _picture.Width = double.NaN;
            if (_size?.Height is not null) _picture.Height = double.NaN;
        }
        if (Math.Abs(_picture.MaxWidth - available) > 0.5) _picture.MaxWidth = available;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (Target is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Handled = true;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var open = this.FindAncestorOfType<MarkdownView>()?.OpenLink;
        if (open?.CanExecute(Target) == true) open.Execute(Target);
    }
}
