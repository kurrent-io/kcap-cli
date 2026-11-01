namespace Capacitor.App.Platform;

/// Where the bundled CLI and Claude plugin sit inside a published app. The Linux and Windows bundles
/// keep the CLI in bin/, the folder that goes on PATH, because the plugin directory is also named kcap:
/// Git Bash, which runs Claude Code's hooks on Windows, does not resolve kcap.exe beside a kcap folder.
/// `kcap` finds the plugin at bin/../kcap. The macOS bundle has no bin/.
public static class BundleLayout {
    public static string CliDirectory(string appDirectory) {
        var bin = Path.Combine(appDirectory, "bin");
        return Directory.Exists(bin) ? bin : appDirectory;
    }

    public static string PluginDirectory(string appDirectory) => Path.Combine(appDirectory, "kcap");
}
