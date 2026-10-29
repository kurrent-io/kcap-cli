using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Platform.Windows;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform;

/// What differs between the platforms the app ships on, decided once at startup, so the rest of
/// the app asks the host instead of testing the operating system.
public abstract class DesktopHost : IDisposable {
    protected DesktopHost(string cliDirectory, bool updateRelaunch, SingleInstanceLock singleInstance) {
        CliDirectory = cliDirectory;
        UpdateRelaunch = updateRelaunch;
        SingleInstance = singleInstance;
    }

    /// Passed to a copy the app starts of itself before exiting: that copy waits for this one to
    /// release its single-instance lock instead of handing over to a process that is shutting down.
    /// Velopack's restart after an update is recognised by its own hook.
    public const string RelaunchArg = "--relaunch";

    // A relaunch starts once the old copy's teardown is over, so this covers only its process exit.
    // Past it, that copy is taken to be still running and is asked to show itself instead.
    protected static readonly TimeSpan RelaunchWait = TimeSpan.FromSeconds(30);

    /// The host for this process, or null when another copy of the app took over this launch and
    /// this process must exit.
    public static DesktopHost? Start(string[] args, bool updateRelaunch) {
        var relaunch = updateRelaunch || args.Contains(RelaunchArg, StringComparer.Ordinal);
        if (SingleInstanceLock.Acquire(relaunch ? RelaunchWait : TimeSpan.Zero) is not { } singleInstance) return null;

        var appDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS()) return new MacDesktopHost(BundleLayout.CliDirectory(appDirectory), updateRelaunch, singleInstance);
        if (OperatingSystem.IsWindows()) return new WindowsDesktopHost(BundleLayout.CliDirectory(appDirectory), updateRelaunch, singleInstance);
        if (OperatingSystem.IsLinux()) return LinuxDesktopHost.Start(appDirectory, updateRelaunch, singleInstance);
        throw new PlatformNotSupportedException();
    }

    /// The host for this platform with nothing locked or copied: what a process that never ran
    /// Start sees, such as the headless test app.
    public static DesktopHost Unstarted() {
        var cliDirectory = BundleLayout.CliDirectory(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS()) return new MacDesktopHost(cliDirectory, false, SingleInstanceLock.Unenforced);
        if (OperatingSystem.IsWindows()) return new WindowsDesktopHost(cliDirectory, false, SingleInstanceLock.Unenforced);
        return new LinuxDesktopHost(cliDirectory, false, null, false, SingleInstanceLock.Unenforced);
    }

    public string CliDirectory { get; }

    /// Velopack relaunched this process after applying an update; its hook fires on a failed apply
    /// too, so this means "relaunched", not "updated".
    public bool UpdateRelaunch { get; }

    /// The tray menu tells the app before it opens. Where it does not, the menu is kept current as
    /// the model changes and the pause state is refreshed on a timer.
    public virtual bool TrayAnnouncesMenuOpen => true;

    /// False when a new copy could not start, so the app must not quit for one.
    public virtual bool CanRelaunchNow => true;

    /// False while the CLI runs from somewhere that vanishes when the app exits, so nothing may link to it.
    public virtual bool CliOutlivesApp => true;

    public virtual AppBuilder ConfigureAvalonia(AppBuilder builder) => builder;

    /// Blocks until the platform can show a window.
    public virtual void WaitUntilReady() { }

    public abstract IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch);

    public SingleInstanceLock SingleInstance { get; }

    /// A copy of this app with args, which waits for this one to release its single-instance lock.
    public ProcessStartInfo NewCopy(params string[] args) {
        var executable = Executable ?? throw new InvalidOperationException("The app's executable could not be found.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(RelaunchArg);
        return start;
    }

    /// Arranges a second copy of this app; the caller then shuts this one down. False when none can
    /// be started, so this one must keep running.
    public virtual Task<bool> StartSecondCopyAsync(IClassicDesktopStyleApplicationLifetime desktop, TimeProvider time, CancellationToken ct) {
        // Checked before shutting down: past that point a start that fails leaves nothing running.
        if (Executable is not { } executable || !File.Exists(executable)) return Task.FromResult(false);
        var start = NewCopy();
        // On Exit, once teardown is over: shutdown can outlast the new copy's wait for the lock.
        desktop.Exit += (_, _) => {
            try { Process.Start(start)?.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: could not relaunch: {ex.Message}"); }
        };
        return Task.FromResult(true);
    }

    protected virtual string? Executable => Environment.ProcessPath;

    public virtual void Dispose() {
        SingleInstance.Dispose();
        GC.SuppressFinalize(this);
    }
}
