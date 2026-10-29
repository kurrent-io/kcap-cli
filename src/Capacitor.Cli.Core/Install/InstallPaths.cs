namespace Capacitor.Cli.Core.Install;

/// <summary>
/// The form of a kcap path that survives an update of the install it belongs to: a script install's
/// <c>current/bin</c>, a snap's <c>current</c>, or the path unchanged. Anything that persists a kcap path —
/// a service unit, an MCP registration, a git hook — goes through this.
/// </summary>
public static class InstallPaths {
    public static string Stabilize(string path, Func<string, bool> fileExists) =>
        SnapLayout.Stabilize(ScriptInstallLayout.Stabilize(path, fileExists), fileExists);

    /// <summary><see cref="Stabilize(string, Func{string, bool})"/> against the real filesystem.</summary>
    public static string Stabilize(string path) => Stabilize(path, File.Exists);
}
