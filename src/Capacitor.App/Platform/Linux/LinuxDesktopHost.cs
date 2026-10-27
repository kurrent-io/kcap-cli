using Avalonia;
using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

public sealed class LinuxDesktopHost(string cliDirectory, bool updateRelaunch, AppImageInstall? appImage, bool cliInMount, SingleInstanceLock singleInstance)
    : DesktopHost(cliDirectory, updateRelaunch, singleInstance) {
    internal static LinuxDesktopHost Start(string appDirectory, bool updateRelaunch, SingleInstanceLock singleInstance) {
        var appImage = AppImageInstall.Detect(Environment.GetEnvironmentVariable);
        var bundleCli    = BundleLayout.CliDirectory(appDirectory);
        var cliDirectory = appImage?.TakeCliOutOfMount(Environment.GetEnvironmentVariable, UserHome.FromEnvironment(), appDirectory)
                           ?? bundleCli;
        return new LinuxDesktopHost(cliDirectory, updateRelaunch, appImage, appImage is not null && cliDirectory == bundleCli, singleInstance);
    }

    public override AppBuilder ConfigureAvalonia(AppBuilder builder) =>
        builder.With(new X11PlatformOptions {
            RenderingMode = LinuxRenderingOrder.Resolve(Environment.GetEnvironmentVariable(LinuxRenderingOrder.EnvVar)),
        });

    public override bool TrayAnnouncesMenuOpen => false;

    /// A new copy starts from the image, which may have been moved or deleted since this one started.
    public override bool CanRelaunchNow => appImage is null || File.Exists(appImage.Image);

    /// A CLI the image could not copy out runs from its mount.
    public override bool CliOutlivesApp => !cliInMount;

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new LinuxDesktopNotificationSink(dispatch);

    /// The AppImage process runs inside a mount that vanishes when it exits; the image does not.
    protected override string? Executable => appImage?.Image ?? Environment.ProcessPath;
}
