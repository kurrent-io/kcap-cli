using System.Text.RegularExpressions;

namespace kapacitor;

/// <summary>
/// Path layout for per-name daemon lock + PID + start-lock files (AI-630).
///
/// <para>The previous layout used singletons at <c>PathHelpers.ConfigPath("agent.pid")</c>
/// and <c>PathHelpers.ConfigPath("agent.start.lock")</c>. Two daemons with
/// different <c>KAPACITOR_CONFIG_DIR</c>s (e.g. an Aspire-spawned dev daemon
/// using <c>.dev/kapacitor</c> alongside a user-launched daemon using
/// <c>~/.config/kapacitor</c>) wrote to different singletons and never saw
/// each other, allowing two daemons under the same name to authenticate as
/// the same GitHub ID and oscillate the server-side <c>DaemonRegistry</c>
/// slot. The staging incident that motivated AI-630 was exactly that.</para>
///
/// <para>This helper uses a <b>fixed location</b> under the home directory
/// (<c>~/.config/kapacitor/daemons/</c>) regardless of <c>KAPACITOR_CONFIG_DIR</c>,
/// so cross-config-dir daemons under the same name now collide on the same
/// <c>flock</c>. Sanitization is intentionally strict to keep filenames
/// portable: only <c>[a-z0-9._-]</c> survives, anything else maps to <c>-</c>.</para>
/// </summary>
public static partial class DaemonLockPaths {
    static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "kapacitor", "daemons"
    );

    static string? _overrideDir;

    /// <summary>Directory where all per-name lock/pid/start files live.</summary>
    public static string Directory => _overrideDir ?? DefaultDirectory;

    /// <summary>
    /// Test-only override: redirects the daemons directory to a temp path.
    /// Pass <c>null</c> to restore the default home-directory location.
    /// Production code never sets this; it's exposed via
    /// <c>InternalsVisibleTo</c> for the test projects only.
    /// </summary>
    internal static void OverrideDirectoryForTesting(string? path) => _overrideDir = path;

    [GeneratedRegex(@"[^a-z0-9._-]")]
    private static partial Regex DisallowedChars();

    /// <summary>
    /// Lowercase the input, replace any character outside <c>[a-z0-9._-]</c>
    /// with <c>-</c>, collapse consecutive separators, and strip leading /
    /// trailing separators. Empty / whitespace-only input falls back to
    /// <c>"daemon"</c> so we never produce a file at the directory root.
    /// </summary>
    public static string Sanitize(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "daemon";

        var lowered    = name.Trim().ToLowerInvariant();
        var normalized = DisallowedChars().Replace(lowered, "-");
        var collapsed  = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? "daemon" : collapsed;
    }

    /// <summary>Path to the daemon-held flock file. Content = instance_id GUID.</summary>
    public static string LockPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.lock");

    /// <summary>Path to the PID file. Content = PID + optional StartTicks line.</summary>
    public static string PidPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.pid");

    /// <summary>
    /// Path to the CLI-side start lock — the brief critical-section lock the
    /// <c>kapacitor daemon start</c> supervisor takes around its
    /// check-spawn-write-PID sequence. Distinct from <see cref="LockPath"/>,
    /// which the daemon itself holds for its entire lifetime.
    /// </summary>
    public static string StartLockPath(string daemonName) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.start");

    /// <summary>Ensures the parent directory exists. Safe to call repeatedly.</summary>
    public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>
    /// Returns the daemon names visible on disk — the union of names
    /// derived from <c>*.lock</c> and <c>*.pid</c> files. Used by
    /// <c>daemon doctor</c> to classify held vs stale entries; covers
    /// orphan PID files that have no matching lock (e.g. a pre-AI-630
    /// daemon whose migration ran for the PID file but not the start
    /// lock, or a stop that removed the lock but left the PID behind).
    /// </summary>
    public static IReadOnlyList<string> EnumerateNames() {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var fromLocks = System.IO.Directory.EnumerateFiles(Directory, "*.lock")
            .Select(Path.GetFileNameWithoutExtension);
        var fromPids  = System.IO.Directory.EnumerateFiles(Directory, "*.pid")
            .Select(Path.GetFileNameWithoutExtension);

        return [
            .. fromLocks.Concat(fromPids)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct()
                .Order()
        ];
    }
}
