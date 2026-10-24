using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerServices {
    /// <param name="systemdRunning">Test seam; production checks <see cref="ServiceManagerFactory.SystemdRunning"/>.</param>
    public static ILocalServerService ForCurrentOs(UserHome home, TimeProvider time, Func<bool>? systemdRunning = null) {
        if (OperatingSystem.IsMacOS()) return new LocalServerLaunchdService(home, time);
        if (OperatingSystem.IsLinux()) {
            // Uninstall and upgrade read this refusal as "nothing installed"; without it their first systemctl throws.
            if (!(systemdRunning ?? ServiceManagerFactory.SystemdRunning)())
                throw new LocalInstallFailure(LocalInstallFailureCode.UnsupportedPlatform,
                    "The local edition on Linux needs systemd, which is not running on this machine.");

            return new LocalServerSystemdService(home, time);
        }

        throw new PlatformNotSupportedException("The local edition runs on macOS and Linux.");
    }
}
