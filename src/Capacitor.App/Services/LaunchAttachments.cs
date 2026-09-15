namespace Capacitor.App.Services;

/// The remote gate for attaching files to a launch: a daemon older than this fetches nothing and
/// would start the session without the files, so the launcher refuses to send ids there.
public static class LaunchAttachments {
    /// The kcap release whose daemon fails a launch closed on an attachment it cannot fetch.
    public static readonly Version MinDaemonVersion = new(1, 0, 4);

    /// A prerelease of the minimum counts: the build that carries the behaviour is what matters,
    /// not whether it shipped. A version this cannot parse is never guessed capable.
    public static bool IsCapable(string? daemonVersion) {
        if (string.IsNullOrWhiteSpace(daemonVersion)) return false;
        var core = daemonVersion.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) && Normalize(parsed) >= MinDaemonVersion;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
