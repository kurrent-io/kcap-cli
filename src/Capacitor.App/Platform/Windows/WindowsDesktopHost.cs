using Avalonia;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Windows;

public sealed class WindowsDesktopHost : DesktopHost {
    readonly SingleInstanceGuard? _instance;

    internal WindowsDesktopHost(string cliDirectory, bool updateRelaunch, SingleInstanceGuard? instance)
        : base(cliDirectory, updateRelaunch) => _instance = instance;

    internal static WindowsDesktopHost? Start(string appDirectory, bool updateRelaunch, bool relaunch) {
        SingleInstanceGuard? instance = null;
        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe) {
            instance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(exe), relaunch ? RelaunchWait : TimeSpan.Zero);
            if (instance is null) return null;
        }
        return new WindowsDesktopHost(BundleLayout.CliDirectory(appDirectory), updateRelaunch, instance);
    }

    public override bool TrayAnnouncesMenuOpen => false;

    // Avalonia.Labs notifications only here: its macOS backend has async-void authorization
    // failures, and its Linux one targets an older DBus library than Avalonia ships.
    public override AppBuilder ConfigureAvalonia(AppBuilder builder) => WindowsDesktopNotificationSink.Configure(builder);

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new WindowsDesktopNotificationSink(dispatch);

    /// The callback runs on a thread-pool thread.
    public override void OnSecondLaunch(Action show) {
        if (OperatingSystem.IsWindows()) _instance?.OnShowRequested(show);
    }

    public override void Dispose() {
        if (OperatingSystem.IsWindows()) _instance?.Dispose();
        base.Dispose();
    }
}
