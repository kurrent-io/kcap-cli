using Avalonia;
using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;
using ReactiveUI.Avalonia.Reactive;
using Velopack;

namespace Capacitor.App;

internal static class Program
{
    /// True when Velopack relaunched this process after applying an update — set from its
    /// OnRestarted hook, which fires on a failed apply too, so it means "relaunched", not "updated".
    public static bool UpdateRelaunch { get; private set; }

    /// Held for the life of the process on Windows; null elsewhere.
    public static SingleInstanceGuard? Instance { get; private set; }

    // A relaunch starts once the old copy's teardown is over, so this covers only its process exit.
    // Past it, that copy is taken to be still running and is asked to show itself instead.
    static readonly TimeSpan RelaunchWait = TimeSpan.FromSeconds(30);

    [STAThread]
    public static void Main(string[] args) {
        // Velopack's install/update hooks exit from inside Run(); anything before it would re-run
        // during those operations. Auto-apply stays off: pending packages are applied by
        // UpdateCoordinator after the install-location guard and the prerelease rule. A snap has
        // no Velopack install to hook: snapd updates it.
        if (SnapLayout.OfRunningBinary() is null) {
            var velopack = VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnRestarted(_ => UpdateRelaunch = true);
            if (OperatingSystem.IsWindows())
                velopack = velopack.OnBeforeUninstallFastCallback(_ => {
                    if (OperatingSystem.IsWindows()) WindowsUninstallCleanup.Run(AppContext.BaseDirectory);
                });
            velopack.Run();
        }

        if (OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe) {
            var relaunch = UpdateRelaunch || args.Contains(SingleInstanceGuard.RelaunchArg, StringComparer.Ordinal);
            Instance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(exe), relaunch ? RelaunchWait : TimeSpan.Zero);
            if (Instance is null) return;
        }

        var crashLog = new AppCrashLog(ConfigRoot.FromEnvironment(), TimeProvider.System);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => {
            if (e.ExceptionObject is Exception ex) crashLog.Record("unhandled", ex);
        };

        if (OperatingSystem.IsMacOS())
            ActiveDisplayGate.WaitAsync(CoreVideoDisplayLink.Probe, TimeProvider.System,
                onWaiting: () => Console.Error.WriteLine("Every display is asleep; the app starts once one wakes."))
                .GetAwaiter().GetResult();

        // Avalonia.Native stops the run loop on an exception from any UI-thread callback and rethrows
        // it from here, bypassing Dispatcher.UnhandledException — so this is the one place that sees
        // every UI-thread fault. The rethrow still ends the process with a crash report.
        try {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        } catch (Exception ex) {
            crashLog.Record("ui-thread", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        NativeDesktopNotificationSink.Configure(AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions {
                RenderingMode = MacRenderingOrder.Resolve(Environment.GetEnvironmentVariable(MacRenderingOrder.EnvVar)),
                AvaloniaNativeLibraryPath = PatchedAvaloniaNative.Path,
            })
            .UseReactiveUI(_ => { })
            .LogToTrace());
}
