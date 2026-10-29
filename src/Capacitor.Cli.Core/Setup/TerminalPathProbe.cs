namespace Capacitor.Cli.Core.Setup;

public static class TerminalPathProbe {
    /// The probe for this OS: Windows has no login shell to ask, and a GUI launch there does not
    /// start from a stripped PATH the way a launchd one does.
    public static ILoginShellProbe Create(IProcessRunner runner, Func<string, string?> getEnv) =>
        OperatingSystem.IsWindows() ? new WindowsPathProbe() : new LoginShellProbe(runner, getEnv);
}
