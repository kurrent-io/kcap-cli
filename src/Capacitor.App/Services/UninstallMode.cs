using Capacitor.App.Platform.Mac;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// A copy of the app started only to uninstall kcap: it builds nothing of the normal app and
/// writes nothing under the config root.
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
            string? processPath, Func<string, IAppRemoval> removalFor, IKcapCli cli, Action quit, Action<Action>? post = null) {
        if (InstallLocation.BundleRoot(processPath) is not { } root
         || !File.Exists(Path.Combine(root, "Contents", "Info.plist"))) return null;
        return new UninstallRunViewModel(cli, removalFor(root), PurgeLocal, quit, post);
    }
}
