using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A settings page column: as wide as the room allows up to its MaxWidth, and anchored left. A
/// left-aligned StackPanel would instead size to its widest child, so every page's cards would end
/// at a different edge; a stretched one centres once it reaches its MaxWidth.
public sealed class SettingsPage : StackPanel {
    protected override Size MeasureOverride(Size availableSize) {
        var desired = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width) ? desired : desired.WithWidth(Math.Max(desired.Width, availableSize.Width));
    }
}
