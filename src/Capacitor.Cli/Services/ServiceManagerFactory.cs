using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(
            ServicePlatform platform, ConfigRoot config, UserHome home, TimeProvider time) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(home, time),
        ServicePlatform.Systemd              => new SystemdServiceManager(home),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(config),
        ServicePlatform.WindowsService when OperatingSystem.IsWindows() => new WindowsServiceManager(
            config, new WindowsServicePipeClient(time), new RegistryServiceEnrollmentStore(), new WindowsScheduledTaskServiceManager(config)),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    /// <summary>On Windows, the machine's <c>kcap-service</c> once it is registered, whether or not it is
    /// answering: falling back to a Scheduled Task beside it would put two supervisors on one name</summary>
    public static ServicePlatform CurrentPlatform() {
        if (OperatingSystem.IsMacOS()) return ServicePlatform.Launchd;
        if (OperatingSystem.IsLinux()) return ServicePlatform.Systemd;
        if (OperatingSystem.IsWindows())
            return WindowsServiceBinaries.Registered() ? ServicePlatform.WindowsService : ServicePlatform.WindowsScheduledTask;
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> where no supported service manager runs,
    /// Linux without systemd included: every caller reads that as "no service", where a systemd manager
    /// would fail on its first <c>systemctl</c>.</summary>
    /// <param name="systemdRunning">Test seam; production checks <see cref="SystemdRunning"/>.</param>
    public static IServiceManager ForCurrentOs(ConfigRoot config, UserHome home, TimeProvider time, Func<bool>? systemdRunning = null) {
        var platform = CurrentPlatform();
        if (platform == ServicePlatform.Systemd && !(systemdRunning ?? SystemdRunning)())
            throw new PlatformNotSupportedException(
                "kcap daemon service needs systemd, which is not running on this machine. Start the daemon with "
              + "`kcap daemon start -d`; nothing starts it again after a restart.");

        return ForPlatform(platform, config, home, time);
    }

    /// <summary>systemd's own <c>sd_booted</c> test. A container or sandbox without it often has no
    /// <c>systemctl</c> at all.</summary>
    public static bool SystemdRunning() => Directory.Exists("/run/systemd/system");

    /// <summary>The word <c>service status --json</c> reports for the manager in charge</summary>
    public static string SupervisorWord(IServiceManager manager) => manager switch {
        LaunchdServiceManager              => "launchd",
        SystemdServiceManager              => "systemd",
        WindowsServiceManager              => "windows_service",
        WindowsScheduledTaskServiceManager => "windows_scheduled_task",
        _                                  => "unknown",
    };
}
