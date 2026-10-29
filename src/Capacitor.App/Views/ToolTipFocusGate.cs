using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// A tooltip is its own popup window, so a pointer crossing an inactive app window would open
/// one above whatever app holds focus. Installed once per application: no tip opens in an
/// inactive window, and a window losing activation closes the tip it shows.
public static class ToolTipFocusGate {
    static bool _installed;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(OnOpening);
        WindowBase.IsActiveProperty.Changed.AddClassHandler<WindowBase>(OnActiveChanged);
    }

    static void OnOpening(Control control, CancelRoutedEventArgs e) {
        if (TopLevel.GetTopLevel(control) is WindowBase { IsActive: false }) e.Cancel = true;
    }

    static void OnActiveChanged(WindowBase window, AvaloniaPropertyChangedEventArgs e) {
        if (window.IsActive) return;
        foreach (var control in window.GetVisualDescendants().OfType<Control>())
            if (ToolTip.GetIsOpen(control)) ToolTip.SetIsOpen(control, false);
    }
}
