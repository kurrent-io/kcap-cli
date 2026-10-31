namespace Capacitor.Cli.Core.Setup;

public static class CliPathInstallers {
    /// The installer for this OS, or null where the app offers none.
    public static ICliPathInstaller? Create(ILoginShellProbe probe, UserHome home) =>
        OperatingSystem.IsMacOS()   ? new PathShimInstaller(probe, home, new ShellProfile(home, Environment.GetEnvironmentVariable("SHELL")))
      : OperatingSystem.IsLinux()   ? new PathShimInstaller(probe, home)
      : OperatingSystem.IsWindows() ? new WindowsUserPathInstaller(new WindowsUserPathStore(), probe)
      : null;
}
