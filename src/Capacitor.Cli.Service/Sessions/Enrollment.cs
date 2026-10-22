namespace Capacitor.Cli.Service.Sessions;

/// <summary>A user's opt-in, read from their own hive. Everything in it is the user's to choose, because the
/// daemon it names only ever runs as that user</summary>
public sealed record Enrollment(string Name, string DaemonPath, string? Profile, int? MaxAgents, string? EnvironmentFile = null) {
    public const string DaemonFileName = "kcap-daemon.exe";

    /// <summary>The <c>kcap.exe</c> shipped beside the daemon, which runs the daemon's own graceful stop</summary>
    public string CliPath => Path.Combine(Path.GetDirectoryName(DaemonPath)!, "kcap.exe");

    /// <summary>Null when the values cannot describe a daemon, with the reason</summary>
    public static Enrollment? Parse(
            string? name, string? daemonPath, string? profile, object? maxAgents, out string? refusal, string? environmentFile = null) {
        refusal = null;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['"', '\\', '/', '\r', '\n']) >= 0) {
            refusal = "the daemon name is missing or not a plain name";
            return null;
        }
        if (string.IsNullOrWhiteSpace(daemonPath) || !IsLocalPath(daemonPath)
            || !string.Equals(Path.GetFileName(daemonPath), DaemonFileName, StringComparison.OrdinalIgnoreCase)) {
            refusal = $"the daemon path is not an absolute local path to {DaemonFileName}";
            return null;
        }
        if (!string.IsNullOrWhiteSpace(environmentFile) && !IsLocalPath(environmentFile)) {
            refusal = "the environment file is not an absolute local path";
            return null;
        }
        int? agents = maxAgents is int n && n >= 0 ? n : null;
        return new Enrollment(name, daemonPath, string.IsNullOrWhiteSpace(profile) ? null : profile, agents,
            string.IsNullOrWhiteSpace(environmentFile) ? null : environmentFile);
    }

    static bool IsLocalPath(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && path.IndexOf('"') < 0;
}
