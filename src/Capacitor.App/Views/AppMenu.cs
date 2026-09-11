using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// Avalonia exports this menu once; update its items without replacing the exported menu.
public sealed class AppMenu {
    readonly NativeMenuItem _settings = new("Settings…") {
        Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        IsEnabled = false,
    };
    Action? _showSettings;

    public AppMenu(Action showAbout) {
        var about = new NativeMenuItem("About Kurrent Capacitor");
        about.Click += (_, _) => showAbout();
        _settings.Click += (_, _) => _showSettings?.Invoke();
        Menu = new NativeMenu { about, new NativeMenuItemSeparator(), _settings };
    }

    public NativeMenu Menu { get; }

    public void SetSettingsAction(Action? showSettings) {
        _showSettings = showSettings;
        _settings.IsEnabled = showSettings is not null;
    }
}
