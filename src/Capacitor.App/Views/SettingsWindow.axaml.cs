using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class SettingsWindow : Window {
    public SettingsWindow() {
        InitializeComponent();
        // Coming back from System Settings is an activation, and the only signal that access changed.
        Activated += (_, _) => (DataContext as SettingsViewModel)?.RefreshNotificationAccess();
    }
}
