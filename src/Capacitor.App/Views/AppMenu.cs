using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// Avalonia exports this menu once; update its items without replacing the exported menu.
public sealed class AppMenu {
    readonly NativeMenuItem _settings = new("Settings…") {
        Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        IsEnabled = false,
    };
    readonly BehaviorSubject<Action?> _showSettings = new(null);
    readonly NativeMenuItem _setup = new("Set up Capacitor…") { IsEnabled = false };
    Action? _showSetup;

    public AppMenu(Action showAbout) {
        var about = new NativeMenuItem("About Kurrent Capacitor");
        about.Click += (_, _) => showAbout();
        _settings.Click += (_, _) => _showSettings.Value?.Invoke();
        _setup.Click += (_, _) => _showSetup?.Invoke();
        Menu = new NativeMenu { about, new NativeMenuItemSeparator(), _settings, _setup };
    }

    public NativeMenu Menu { get; }

    /// The current Settings action, for the in-window copy of this menu off macOS, where no native
    /// app menu is drawn.
    public IObservable<Action?> SettingsAction => _showSettings;

    public void SetSettingsAction(Action? showSettings) {
        _settings.IsEnabled = showSettings is not null;
        _showSettings.OnNext(showSettings);
    }

    public void SetSetupAction(Action? showSetup) {
        _showSetup = showSetup;
        _setup.IsEnabled = showSetup is not null;
    }
}
