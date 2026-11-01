using Avalonia;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.Platform;
using Capacitor.App.Platform.Windows;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;
using ReactiveUI.Avalonia.Reactive;
using Velopack;

namespace Capacitor.App;

internal static class Program
{
    /// This process's platform, chosen once before the UI starts.
    public static DesktopHost Host { get; private set; } = DesktopHost.Unstarted();

    /// Read before the app is constructed, which is where its first config-root write would happen.
    internal static UninstallMode? Uninstall { get; private set; }

    static AppCrashLog? _crashLog;

    internal static void StopCrashLoggingForUninstall() => _crashLog = null;

    [STAThread]
    public static void Main(string[] args) {
        // Velopack's install/update hooks exit from inside Run(); anything before it would re-run
        // during those operations. Auto-apply stays off: pending packages are applied by
        // UpdateCoordinator after the install-location guard and the prerelease rule. A snap has
        // no Velopack install to hook: snapd updates it.
        var updateRelaunch = false;
        if (SnapLayout.OfRunningBinary() is null) {
            var velopack = VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnRestarted(_ => updateRelaunch = true);
            if (OperatingSystem.IsWindows())
                velopack = velopack.OnBeforeUninstallFastCallback(_ => {
                    if (OperatingSystem.IsWindows()) WindowsUninstallCleanup.Run(AppContext.BaseDirectory, UserHome.FromEnvironment());
                });
            velopack.Run();
        }
        Uninstall = UninstallMode.Parse(args);

        // The uninstall deletes the config directory, which a crash log would recreate.
        if (Uninstall is null) {
            _crashLog = new AppCrashLog(ConfigRoot.FromEnvironment(), TimeProvider.System);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => {
                if (e.ExceptionObject is Exception ex) _crashLog?.Record("unhandled", ex);
            };
        }

        if (DesktopHost.Start(args, updateRelaunch) is not { } host) return;
        Host = host;

        host.WaitUntilReady();

        // Avalonia.Native stops the run loop on an exception from any UI-thread callback and rethrows
        // it from here, bypassing Dispatcher.UnhandledException — so this is the one place that sees
        // every UI-thread fault. The rethrow still ends the process with a crash report.
        try {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        } catch (Exception ex) {
            _crashLog?.Record("ui-thread", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        Host.ConfigureAvalonia(AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithAppFont()
            .UseReactiveUI(_ => { })
            .LogToTrace());

    internal static AppBuilder WithAppFont(this AppBuilder builder) =>
        builder.WithInterFont().With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
}
