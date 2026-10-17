using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

public sealed class LinuxDesktopHost : DesktopHost {
    readonly AppImageInstall? _appImage;
    readonly SingleInstance? _instance;

    internal LinuxDesktopHost(string cliDirectory, bool updateRelaunch, AppImageInstall? appImage, SingleInstance? instance)
        : base(cliDirectory, updateRelaunch) {
        _appImage = appImage;
        _instance = instance;
    }

    internal static LinuxDesktopHost? Start(string appDirectory, bool updateRelaunch, bool relaunch) {
        if (!ClaimInstance(relaunch, out var instance)) return null;

        var appImage = AppImageInstall.Detect(Environment.GetEnvironmentVariable);
        var cliDirectory = appImage?.TakeCliOutOfMount(Environment.GetEnvironmentVariable, UserHome.FromEnvironment(), appDirectory)
                           ?? BundleLayout.CliDirectory(appDirectory);
        return new LinuxDesktopHost(cliDirectory, updateRelaunch, appImage, instance);
    }

    public override bool TrayAnnouncesMenuOpen => false;

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new LinuxDesktopNotificationSink(dispatch);

    public override void OnSecondLaunch(Action show) => _instance?.Listen(show);

    /// The AppImage process runs inside a mount that vanishes when it exits; the image does not.
    protected override string? Executable => _appImage?.Image ?? Environment.ProcessPath;

    public override void Dispose() {
        _instance?.Dispose();
        base.Dispose();
    }

    // A broken runtime directory must not stop the app starting, only lose the guard.
    static bool ClaimInstance(bool relaunch, out SingleInstance? instance) {
        instance = null;
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            : ConfigRoot.UnderHome(UserHome.FromEnvironment().Path).Directory;
        if (!SingleInstance.FitsIn(directory)) {
            Console.Error.WriteLine($"kcap: single-instance check skipped: {directory} is too deep for its socket");
            return true;
        }
        try {
            instance = SingleInstance.Claim(directory, relaunch ? RelaunchWait : TimeSpan.Zero, TimeProvider.System);
            return instance is not null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException) {
            Console.Error.WriteLine($"kcap: single-instance check skipped: {ex.Message}");
            return true;
        }
    }
}
