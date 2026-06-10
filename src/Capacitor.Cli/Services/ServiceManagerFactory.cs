namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(ServicePlatform platform) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(),
        ServicePlatform.Systemd              => new SystemdServiceManager(),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    public static IServiceManager ForCurrentOs() {
        if (OperatingSystem.IsMacOS())   return ForPlatform(ServicePlatform.Launchd);
        if (OperatingSystem.IsLinux())   return ForPlatform(ServicePlatform.Systemd);
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }
}
