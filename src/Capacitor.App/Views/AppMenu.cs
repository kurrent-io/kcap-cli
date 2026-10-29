using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services.Update;

namespace Capacitor.App.Views;

/// Avalonia exports this menu once; update its items without replacing the exported menu.
public sealed class AppMenu {
    readonly NativeMenuItem _settings = new("Settings…") {
        Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta),
        IsEnabled = false,
    };
    readonly BehaviorSubject<Action?> _showSettings = new(null);
    // A fixed label: renaming an exported item makes Avalonia rebuild the native menu.
    readonly NativeMenuItem _checkForUpdates = new(UpdateCoordinator.CheckLabel) { IsEnabled = false };
    readonly BehaviorSubject<Func<Task>?> _runCheckForUpdates = new(null);
    readonly NativeMenuItem _setup = new("Set up kcap…") { IsEnabled = false };
    Action? _showSetup;
    readonly NativeMenuItem _uninstall = new("Uninstall kcap…") { IsEnabled = false };
    readonly BehaviorSubject<Action?> _runUninstall = new(null);

    public AppMenu(Action showAbout) {
        var about = new NativeMenuItem("About kcap");
        about.Click += (_, _) => showAbout();
        _checkForUpdates.Click += (_, _) => _ = _runCheckForUpdates.Value?.Invoke();
        _settings.Click += (_, _) => _showSettings.Value?.Invoke();
        _setup.Click += (_, _) => _showSetup?.Invoke();
        _uninstall.Click += (_, _) => _runUninstall.Value?.Invoke();
        Menu = new NativeMenu { about, _checkForUpdates, new NativeMenuItemSeparator(), _settings, _setup };
        if (OperatingSystem.IsMacOS()) {
            Menu.Add(new NativeMenuItemSeparator());
            Menu.Add(_uninstall);
        }
    }

    public NativeMenu Menu { get; }

    /// The current Settings action, for the in-window copy of this menu off macOS, where no native
    /// app menu is drawn.
    public IObservable<Action?> SettingsAction => _showSettings;

    public void SetSettingsAction(Action? showSettings) {
        _settings.IsEnabled = showSettings is not null;
        _showSettings.OnNext(showSettings);
    }

    /// The current update check, for the in-window copy of this menu off macOS.
    public IObservable<Func<Task>?> CheckForUpdatesAction => _runCheckForUpdates;

    public void SetCheckForUpdatesAction(Func<Task>? check) {
        _checkForUpdates.IsEnabled = check is not null;
        _runCheckForUpdates.OnNext(check);
    }

    public void SetSetupAction(Action? showSetup) {
        _showSetup = showSetup;
        _setup.IsEnabled = showSetup is not null;
    }

    /// The current uninstall action, for the in-window copy of this menu off macOS.
    public IObservable<Action?> UninstallAction => _runUninstall;

    public void SetUninstallAction(Action? uninstall) {
        _uninstall.IsEnabled = uninstall is not null;
        _runUninstall.OnNext(uninstall);
    }
}
