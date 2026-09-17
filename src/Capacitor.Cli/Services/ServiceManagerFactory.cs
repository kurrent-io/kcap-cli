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

    public static IServiceManager ForCurrentOs(ConfigRoot config, UserHome home, TimeProvider time) {
        if (OperatingSystem.IsMacOS())   return ForPlatform(ServicePlatform.Launchd, config, home, time);
        if (OperatingSystem.IsLinux())   return ForPlatform(ServicePlatform.Systemd, config, home, time);
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask, config, home, time);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }
}
