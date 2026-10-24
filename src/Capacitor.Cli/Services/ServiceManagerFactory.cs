using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(
            ServicePlatform platform, ConfigRoot config, UserHome home, TimeProvider time) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(home, time),
        ServicePlatform.Systemd              => new SystemdServiceManager(home),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(config),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> where no supported service manager runs,
    /// Linux without systemd included: every caller reads that as "no service", where a systemd manager
    /// would fail on its first <c>systemctl</c>.</summary>
    /// <param name="systemdRunning">Test seam; production checks <see cref="SystemdRunning"/>.</param>
    public static IServiceManager ForCurrentOs(ConfigRoot config, UserHome home, TimeProvider time, Func<bool>? systemdRunning = null) {
        if (OperatingSystem.IsMacOS()) return ForPlatform(ServicePlatform.Launchd, config, home, time);
        if (OperatingSystem.IsLinux()) {
            if (!(systemdRunning ?? SystemdRunning)())
                throw new PlatformNotSupportedException(
                    "kcap daemon service needs systemd, which is not running on this machine. Start the daemon with "
                  + "`kcap daemon start -d`; nothing starts it again after a restart.");

            return ForPlatform(ServicePlatform.Systemd, config, home, time);
        }
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask, config, home, time);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }

    /// <summary>systemd's own <c>sd_booted</c> test. A container or sandbox without it often has no
    /// <c>systemctl</c> at all.</summary>
    public static bool SystemdRunning() => Directory.Exists("/run/systemd/system");
}
