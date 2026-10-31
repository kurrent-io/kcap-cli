using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerServices {
    /// <summary>Without systemd every <c>systemctl</c> call fails as an unreachable systemd does, so an
    /// installation on disk still reads as installed: uninstall must not take it for none and delete its data.</summary>
    /// <param name="systemdRunning">Test seam; production checks <see cref="ServiceManagerFactory.SystemdRunning"/>.</param>
    public static ILocalServerService ForCurrentOs(UserHome home, TimeProvider time, Func<bool>? systemdRunning = null) {
        if (OperatingSystem.IsMacOS()) return new LocalServerLaunchdService(home, time);
        if (OperatingSystem.IsLinux())
            return (systemdRunning ?? ServiceManagerFactory.SystemdRunning)()
                ? new LocalServerSystemdService(home, time)
                : new LocalServerSystemdService(home, time, run: NoSystemd);
        if (OperatingSystem.IsWindows()) return new LocalServerTaskService(home, time);

        throw new PlatformNotSupportedException("The local edition runs on macOS, Linux and Windows.");
    }

    static (int ExitCode, string StdOut, string StdErr) NoSystemd(string file, string[] args) =>
        (127, "", "systemd is not running on this machine");
}
