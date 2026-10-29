namespace Capacitor.App.Platform;

/// Where the bundled CLI and Claude plugin sit inside a published app. The Linux bundle keeps the
/// CLI in bin/ because the plugin directory beside it is also named kcap; `kcap` finds the plugin at
/// bin/../kcap. Other bundles have no bin/ and keep the CLI beside the app.
public static class BundleLayout {
    public static string CliDirectory(string appDirectory) {
        var bin = Path.Combine(appDirectory, "bin");
        return Directory.Exists(bin) ? bin : appDirectory;
    }

    public static string PluginDirectory(string appDirectory) => Path.Combine(appDirectory, "kcap");
}
