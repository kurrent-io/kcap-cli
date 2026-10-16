using Capacitor.Cli.Core;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerServices {
    public static ILocalServerService ForCurrentOs(UserHome home, TimeProvider time) {
        if (OperatingSystem.IsMacOS()) return new LocalServerLaunchdService(home, time);
        if (OperatingSystem.IsLinux()) return new LocalServerSystemdService(home, time);

        throw new PlatformNotSupportedException("The local edition runs on macOS and Linux.");
    }
}
