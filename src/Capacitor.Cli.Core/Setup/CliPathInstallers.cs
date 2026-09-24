namespace Capacitor.Cli.Core.Setup;

public static class CliPathInstallers {
    /// The installer for this OS, or null where the app offers none (Linux: a package manager or
    /// the npm install owns PATH there).
    public static ICliPathInstaller? Create(IProcessRunner runner, ILoginShellProbe probe) {
        if (OperatingSystem.IsMacOS()) return new PathShimInstaller(runner, probe);
        if (OperatingSystem.IsWindows()) return new WindowsUserPathInstaller(new WindowsUserPathStore(), probe);
        return null;
    }
}
