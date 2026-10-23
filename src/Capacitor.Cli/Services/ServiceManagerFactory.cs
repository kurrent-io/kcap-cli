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

    public static IServiceManager ForCurrentOs(ConfigRoot config, UserHome home, TimeProvider time) =>
        ForPlatform(CurrentPlatform(), config, home, time);

    /// <summary>The word <c>service status --json</c> reports for the manager in charge</summary>
    public static string SupervisorWord(IServiceManager manager) => manager switch {
        LaunchdServiceManager              => "launchd",
        SystemdServiceManager              => "systemd",
        WindowsServiceManager              => "windows_service",
        WindowsScheduledTaskServiceManager => "windows_scheduled_task",
        _                                  => "unknown",
    };
}
