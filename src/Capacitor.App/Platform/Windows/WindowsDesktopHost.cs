using Avalonia;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Windows;

public sealed class WindowsDesktopHost(string cliDirectory, bool updateRelaunch, SingleInstanceLock singleInstance)
    : DesktopHost(cliDirectory, updateRelaunch, singleInstance) {
    public override bool TrayAnnouncesMenuOpen => false;

    // Avalonia.Labs notifications only here: its macOS backend has async-void authorization
    // failures, and its Linux one targets an older DBus library than Avalonia ships.
    public override AppBuilder ConfigureAvalonia(AppBuilder builder) => WindowsDesktopNotificationSink.Configure(builder);

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new WindowsDesktopNotificationSink(dispatch);
}
