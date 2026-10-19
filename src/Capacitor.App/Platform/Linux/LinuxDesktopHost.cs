using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

public sealed class LinuxDesktopHost(string cliDirectory, bool updateRelaunch, AppImageInstall? appImage, SingleInstanceLock singleInstance)
    : DesktopHost(cliDirectory, updateRelaunch, singleInstance) {
    internal static LinuxDesktopHost Start(string appDirectory, bool updateRelaunch, SingleInstanceLock singleInstance) {
        var appImage = AppImageInstall.Detect(Environment.GetEnvironmentVariable);
        var cliDirectory = appImage?.TakeCliOutOfMount(Environment.GetEnvironmentVariable, UserHome.FromEnvironment(), appDirectory)
                           ?? BundleLayout.CliDirectory(appDirectory);
        return new LinuxDesktopHost(cliDirectory, updateRelaunch, appImage, singleInstance);
    }

    public override bool TrayAnnouncesMenuOpen => false;

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new LinuxDesktopNotificationSink(dispatch);

    /// The AppImage process runs inside a mount that vanishes when it exits; the image does not.
    protected override string? Executable => appImage?.Image ?? Environment.ProcessPath;
}
