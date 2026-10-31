using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Platform.Windows;

/// What uninstalling the app undoes on Windows: the Scheduled Tasks that run this install's daemon, whose wrapper
/// would otherwise retry the deleted binary at every sign-in, the local server this app's CLI installed (its data
/// kept), and the user PATH entry the app added. Best effort: Velopack kills the hook at 30 s, so each step has its
/// own budget inside that, the quick PATH removal goes last, and the uninstall goes ahead whatever happens here.
public static class WindowsUninstallCleanup {
    internal static readonly TimeSpan DaemonStepTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LocalStepTimeout  = TimeSpan.FromSeconds(15);

    [SupportedOSPlatform("windows")]
    public static void Run(string appDirectory, UserHome home) {
        var cli = Path.Combine(BundleLayout.CliDirectory(appDirectory), CliResolver.BundledCliFileName);
        Run(cli, appDirectory, LocalRoot.FromEnvironment(home), RunBounded, () => RemovePath(cli));
    }

    /// <param name="runCli">Runs the CLI with these arguments, its tree killed once the budget is spent.</param>
    internal static void Run(
            string cli, string appDirectory, LocalRoot local, Action<string, string[], TimeSpan> runCli, Action removePath) {
        if (File.Exists(cli)) {
            runCli(cli, ["daemon", "service", "uninstall", "--under", appDirectory], DaemonStepTimeout);
            if (LocalInstalled(local)) runCli(cli, ["local", "uninstall", "--yes", "--force", "--quick"], LocalStepTimeout);
        }

        removePath();
    }

    // A journal without a registry is a setup that never finished, which still left a server to remove.
    static bool LocalInstalled(LocalRoot local) => File.Exists(local.RegistryPath) || File.Exists(local.JournalPath);

    [SupportedOSPlatform("windows")]
    static void RemovePath(string cli) {
        try {
            new WindowsUserPathInstaller(new WindowsUserPathStore(), new WindowsPathProbe()).Remove(cli);
        } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) {
        }
    }

    static void RunBounded(string cli, string[] args, TimeSpan budget) {
        try {
            using var process = Process.Start(new ProcessStartInfo(cli, args) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process is null) return;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(budget)) return;
            // Left running, it would hold files in the folder Velopack is about to delete.
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
        }
    }
}
