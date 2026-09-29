using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Full-width row host that sizes its card child to the chat column up to <see cref="WidthCap"/>,
/// always left-aligned — Stretch+MaxWidth on the card itself would center the slack in Avalonia.
public partial class ChatCardRow : ContentControl {
    public const double DefaultWidthCap = 660;

    public static readonly StyledProperty<double> WidthCapProperty =
        AvaloniaProperty.Register<ChatCardRow, double>(nameof(WidthCap), DefaultWidthCap);

    public double WidthCap {
        get => GetValue(WidthCapProperty);
        set => SetValue(WidthCapProperty, value);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) {
        base.OnSizeChanged(e);
        ApplyChildWidth();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty || change.Property == WidthCapProperty)
            ApplyChildWidth();
    }

    void ApplyChildWidth() {
        if (Content is not Control child || Bounds.Width <= 0) return;
        var width = Math.Min(Bounds.Width, WidthCap);
        child.MaxWidth = WidthCap;
        child.Width = width;
        child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }
}
