using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) => RailHelpButton.Flyout?.Hide();

    AboutWindow? _about;

    void OnAboutClick(object? sender, RoutedEventArgs e) {
        RailHelpButton.Flyout?.Hide();
        if (_about is { } open) {
            open.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _about = new AboutWindow { DataContext = DataContext };
        _about.Closed += (_, _) => _about = null;
        _about.Show(owner);
    }
}
