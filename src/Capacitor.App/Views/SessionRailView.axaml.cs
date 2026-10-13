using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    // Posted: Click runs before the button's command, and closing the flyout detaches the item,
    // which nulls its inherited DataContext and with it the command about to run.
    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => RailHelpButton.Flyout?.Hide());

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
