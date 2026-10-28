using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Capacitor.Cli.Services;

/// <summary>Runs <c>kcap-service.exe install|uninstall</c> elevated, behind the UAC prompt. The exit code is
/// the elevated command's, or <see cref="DeclinedExit"/> when the prompt was declined</summary>
[SupportedOSPlatform("windows")]
static class WindowsServiceElevation {
    /// <summary>Win32 ERROR_CANCELLED, which a declined UAC prompt reports</summary>
    public const int DeclinedExit = 1223;

    public static int Run(string verb) {
        var exe = WindowsServiceBinaries.Bundled() ?? (File.Exists(WindowsServiceBinaries.Installed) ? WindowsServiceBinaries.Installed : null);
        if (exe is null) {
            Console.Error.WriteLine("This kcap install does not carry kcap-service.exe; the desktop app installs the Windows service.");
            return 1;
        }

        try {
            using var process = Process.Start(new ProcessStartInfo(exe, verb) {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        } catch (Win32Exception ex) when (ex.NativeErrorCode == DeclinedExit) {
            return DeclinedExit;
        }
    }
}
