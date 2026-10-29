using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class AccountsSettingsView : UserControl {
    FlyoutBase? _openMenu;

    public AccountsSettingsView() => InitializeComponent();

    void OnMenuOpened(object? sender, EventArgs e) => _openMenu = sender as FlyoutBase;

    void OnAddMenuOpening(object? sender, EventArgs e) => (DataContext as AccountsSettingsViewModel)?.RefreshCandidates();

    // The command and its parameter are read before the menu hides: closed, its content leaves the
    // tree and its bindings let go of them.
    void OnMenuItemClick(object? sender, RoutedEventArgs e) {
        e.Handled = true;
        var (command, parameter) = sender is Button button ? (button.Command, button.CommandParameter) : (null, null);
        _openMenu?.Hide();
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }
}
