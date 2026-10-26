using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class SettingsWindow : Window {
    public SettingsWindow() {
        InitializeComponent();
        // Coming back from System Settings is an activation, and the only signal that access changed.
        Activated += (_, _) => (DataContext as SettingsViewModel)?.RefreshNotificationAccess();
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnProfileCardClick(object? sender, RoutedEventArgs e) {
        if (ProfilesTab.IsVisible) SettingsTabs.SelectedItem = ProfilesTab;
    }

    public void ShowSearch() => SettingsTabs.SelectedItem = SearchTab;
}
