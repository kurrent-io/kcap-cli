using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Platform.Windows;

/// What uninstalling the app undoes on Windows: the Scheduled Tasks that run this install's daemon,
/// whose wrapper would otherwise retry the deleted binary at every sign-in, and the user PATH entry
/// the app added. Best effort: Velopack kills a hook that runs too long, and the uninstall must go
/// ahead whatever happens here.
[SupportedOSPlatform("windows")]
public static class WindowsUninstallCleanup {
    static readonly TimeSpan ServiceStepTimeout = TimeSpan.FromSeconds(15);

    public static void Run(string appDirectory) {
        var cli = Path.Combine(appDirectory, CliResolver.BundledCliFileName);
        if (File.Exists(cli)) RemoveServices(cli, appDirectory);

        try {
            new WindowsUserPathInstaller(new WindowsUserPathStore(), new WindowsPathProbe()).Remove(cli);
        } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) {
        }
    }

    static void RemoveServices(string cli, string appDirectory) {
        try {
            using var process = Process.Start(new ProcessStartInfo(cli, ["daemon", "service", "uninstall", "--under", appDirectory]) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process is null) return;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(ServiceStepTimeout)) return;
            // Left running, it would hold files in the folder Velopack is about to delete.
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
        }
    }
}
