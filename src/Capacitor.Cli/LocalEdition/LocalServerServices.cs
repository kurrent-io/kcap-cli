using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerServices {
    /// <summary>A registered service that never answers costs every <c>kcap local</c> command this long.</summary>
    static readonly TimeSpan CapabilityWait = TimeSpan.FromSeconds(2);

    /// <summary>Without systemd every <c>systemctl</c> call fails as an unreachable systemd does, so an
    /// installation on disk still reads as installed: uninstall must not take it for none and delete its data.</summary>
    /// <param name="systemdRunning">Test seam; production checks <see cref="ServiceManagerFactory.SystemdRunning"/>.</param>
    public static ILocalServerService ForCurrentOs(UserHome home, TimeProvider time, Func<bool>? systemdRunning = null) {
        if (OperatingSystem.IsMacOS()) return new LocalServerLaunchdService(home, time);
        if (OperatingSystem.IsLinux())
            return (systemdRunning ?? ServiceManagerFactory.SystemdRunning)()
                ? new LocalServerSystemdService(home, time)
                : new LocalServerSystemdService(home, time, run: NoSystemd);
        if (OperatingSystem.IsWindows()) return ForWindows(home, time);

        throw new PlatformNotSupportedException("The local edition runs on macOS, Linux and Windows.");
    }

    [SupportedOSPlatform("windows")]
    static ILocalServerService ForWindows(UserHome home, TimeProvider time) {
        var task        = new LocalServerTaskService(home, time);
        var enrollments = new RegistryLocalServerEnrollmentStore();
        var pipe        = new WindowsServicePipeClient(time);

        return UsesService(enrollments, WindowsServiceBinaries.Registered, pipe)
            ? new LocalServerWindowsService(home, time, pipe, enrollments, task)
            : task;
    }

    /// <summary>An enrollment is kept to whatever the service is doing, answering or not: a task beside it would put
    /// two supervisors on one root. Otherwise the service is used only once it is registered and runs servers.</summary>
    internal static bool UsesService(ILocalServerEnrollmentStore enrollments, Func<bool> registered, IServicePipe pipe) =>
        enrollments.Read() is not null || (registered() && ServicePipeVerbs.ServesLocalServer(pipe, CapabilityWait));

    static (int ExitCode, string StdOut, string StdErr) NoSystemd(string file, string[] args) =>
        (127, "", "systemd is not running on this machine");
}
