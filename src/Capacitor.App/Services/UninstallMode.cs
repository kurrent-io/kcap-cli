using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// Options shared by the app menu and a standalone --uninstall launch. The standalone mode
/// builds none of the normal app; the menu supplies preparation to tear that app down first.
internal sealed record UninstallMode(bool PurgeLocal) {
    public const string Arg = "--uninstall";
    public const string PurgeLocalArg = "--purge-local";

    public static UninstallMode? Parse(string[]? args) =>
        args?.Contains(Arg, StringComparer.Ordinal) == true
            ? new UninstallMode(args.Contains(PurgeLocalArg, StringComparer.Ordinal))
            : null;

    /// Null unless the process runs from a real bundle: the removal trashes the bundle root, so a
    /// root that is merely computed from a path must never reach it.
    public UninstallRunViewModel? BuildRun(
            string? processPath, Func<string, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null) {
        if (InstallLocation.BundleRoot(processPath) is not { } root
         || !File.Exists(Path.Combine(root, "Contents", "Info.plist"))) return null;
        return new UninstallRunViewModel(cli, removalFor(root), PurgeLocal, quit, post, prepare);
    }

    /// Null unless the process runs from an AppImage that is still on disk: the removal trashes the image, so
    /// only the file the running app came from may reach it.
    public UninstallRunViewModel? BuildAppImageRun(
            AppImageInstall? install, Func<AppImageInstall, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null) =>
        install is not null && File.Exists(install.Image)
            ? new UninstallRunViewModel(cli, removalFor(install), PurgeLocal, quit, post, prepare)
            : null;
}
