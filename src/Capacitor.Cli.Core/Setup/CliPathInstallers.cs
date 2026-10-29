namespace Capacitor.Cli.Core.Setup;

public static class CliPathInstallers {
    /// The installer for this OS, or null where the app offers none.
    public static ICliPathInstaller? Create(IProcessRunner runner, ILoginShellProbe probe, UserHome home) =>
        OperatingSystem.IsMacOS()   ? new PathShimInstaller(runner, probe, home: home,
                                          profile: new ShellProfile(home, Environment.GetEnvironmentVariable("SHELL")))
      : OperatingSystem.IsLinux()   ? new PathShimInstaller(runner, probe, Path.Combine(home.Path, ".local", "bin", "kcap"), home)
      : OperatingSystem.IsWindows() ? new WindowsUserPathInstaller(new WindowsUserPathStore(), probe)
      : null;
}
