using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>
/// Where every daemon on this machine keeps its on-disk state, and what each piece is called.
/// Ask it for a path by daemon name. It reads or writes those files only to list names and to
/// remove them at uninstall.
/// Passed explicitly to everything that needs it — there is no ambient default.
///
/// <para>The production directory is a <b>fixed location</b> under the home directory
/// (<c>~/.config/kcap/daemons/</c>) regardless of <c>KCAP_CONFIG_DIR</c>. The previous layout
/// derived it from the config directory, so two daemons with different <c>KCAP_CONFIG_DIR</c>s (e.g. an
/// Aspire-spawned dev daemon using <c>.dev/kcap</c> alongside a user-launched daemon using
/// <c>~/.config/kcap</c>) wrote to different files, never saw each other, and could authenticate as
/// the same GitHub ID and oscillate the server-side <c>DaemonRegistry</c> slot. Pinning the location
/// makes cross-config-dir daemons under the same name collide on the same <c>flock</c>, which is the
/// point: the staging incident that motivated it was exactly that.</para>
/// </summary>
public sealed partial class DaemonStore(string directory) {
    /// <summary>Read only by <see cref="FromEnvironment"/>, at a process entry point. A transport for
    /// handing a directory to a child, not a source of truth — consulting it downstream would restore
    /// the process-global fallback this type exists to remove.</summary>
    public const string DaemonsDirEnvVar = "KCAP_DAEMONS_DIR";

    /// <summary>Directory where all per-name lock/pid/marker/socket files live.</summary>
    public string Directory { get; } = directory;

    /// <summary>The context for this process. Call once, in <c>Main</c> or the composition root.</summary>
    public static DaemonStore FromEnvironment() {
        if (Environment.GetEnvironmentVariable(DaemonsDirEnvVar) is { Length: > 0 } configured)
            return new(configured);

        return new(DefaultDirectory(UserHome.FromEnvironment()));
    }

    /// <summary>The fixed location every daemon uses unless <see cref="DaemonsDirEnvVar"/> moves it.
    /// kcap owns everything in it.</summary>
    public static string DefaultDirectory(UserHome home) => Path.Combine(home.Path, ".config", "kcap", "daemons");

    [GeneratedRegex(@"[^a-z0-9._-]")]
    private static partial Regex DisallowedChars();

    /// <summary>
    /// Reduces a name to <c>[a-z0-9._-]</c>, strictly, to keep filenames portable. Blank input falls
    /// back to <c>"daemon"</c> so we never write to the daemons directory root itself.
    ///
    /// <para>Public because the same reduced form is the id of the daemon's OS service unit — the
    /// launchd label and the on-disk marker names must agree or <c>--verify</c> locks a different file
    /// than the one it inspects.</para>
    /// </summary>
    public static string Sanitize(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "daemon";

        var lowered    = name.Trim().ToLowerInvariant();
        var normalized = DisallowedChars().Replace(lowered, "-");
        var collapsed  = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? "daemon" : collapsed;
    }

    const string LockExtension = "lock";
    const string PidExtension = "pid";
    const string StartExtension = "start";
    const string RestartPendingExtension = "restart-pending";
    const string VersionExtension = "version";
    const string SocketExtension = "sock";
    const string ServiceLockExtension = "service-lock";
    const string ServiceTxnExtension = "service-txn";

    /// <summary>The suffixes of the files something holds an exclusive lock on: the daemon for its
    /// lifetime, <c>daemon start</c> around a spawn, a service transaction around an install.</summary>
    static readonly string[] LockExtensions = [LockExtension, StartExtension, ServiceLockExtension];

    /// <summary>The daemon-held flock file. Content = instance_id GUID.</summary>
    public string LockPath(string daemonName) => File(daemonName, LockExtension);

    /// <summary>The PID file. Content = PID + optional StartTicks line.</summary>
    public string PidPath(string daemonName) => File(daemonName, PidExtension);

    /// <summary>
    /// The CLI-side start lock — the brief critical-section lock the <c>kcap daemon start</c>
    /// supervisor takes around its check-spawn-write-PID sequence. Distinct from
    /// <see cref="LockPath"/>, which the daemon itself holds for its entire lifetime.
    /// </summary>
    public string StartLockPath(string daemonName) => File(daemonName, StartExtension);

    /// <summary>The "restart pending" marker (queued restart-after-update state).</summary>
    public string RestartPendingPath(string daemonName) => File(daemonName, RestartPendingExtension);

    /// <summary>
    /// The version marker — freely readable (unlike the exclusively-flocked <see cref="LockPath"/>)
    /// and holding the running daemon's version, so <c>kcap daemon status</c> can report it without a
    /// socket round-trip.
    /// </summary>
    public string VersionPath(string daemonName) => File(daemonName, VersionExtension);

    /// <summary>Local control socket, colocated with the lock/pid files. macOS caps the whole path at
    /// 103 chars, and a long temp directory plus a long name silently stops binding.</summary>
    public string SocketPath(string daemonName) => File(daemonName, SocketExtension);

    /// <summary>The cross-process lock around one service transaction (install/replace/start).</summary>
    public string ServiceLockPath(string daemonName) => File(daemonName, ServiceLockExtension);

    /// <summary>The crash-visible record of an in-flight service transaction.</summary>
    public string ServiceTxnPath(string daemonName) => File(daemonName, ServiceTxnExtension);

    /// <summary>The consent decision log. Under the state directory rather than a suffixed file,
    /// because it rotates.</summary>
    public string ConsentLogPath(string daemonName) =>
        Path.Combine(StateDirectory(daemonName), "consent-decisions.jsonl");

    /// <summary>The boot-refusal marker, owned end to end by <see cref="BootRefusalMarker"/>.</summary>
    public string BootRefusalPath(string daemonName) =>
        Path.Combine(StateDirectory(daemonName), "boot-refusal.json");

    /// <summary>The rename fence's commit marker, owned end to end by the daemon's admission fence.</summary>
    public string RetiringMarkerPath(string daemonName) =>
        Path.Combine(StateDirectory(daemonName), "retiring.json");

    /// <summary>The per-daemon resume ledger directory.</summary>
    public string ResumeDirectory(string daemonName) => Path.Combine(StateDirectory(daemonName), "resume");

    /// <summary>The liveness heartbeat file.</summary>
    public string HeartbeatPath(string daemonName) => File(daemonName, "heartbeat");

    /// <summary>The per-daemon state directory.</summary>
    public string StateDirectory(string daemonName) => Path.Combine(Directory, Sanitize(daemonName));

    string File(string daemonName, string extension) =>
        Path.Combine(Directory, $"{Sanitize(daemonName)}.{extension}");

    /// <summary>
    /// Deletes everything in the directory, then the directory, or nothing at all when a daemon, a start
    /// or a service transaction holds one of its locks. All three locks of every name an entry could
    /// carry are held until the rest is gone, so none can be taken meanwhile; unlinking a held lock lets
    /// the next taker lock a fresh file. A refused cleanup can leave lock files it created, which are
    /// inert. A link is removed itself, never followed. Entries that appear after the snapshot are left. Call it only on a directory kcap owns, which
    /// <see cref="DefaultDirectory"/> is. Returns whether the directory is gone.
    /// </summary>
    public bool DeleteAll() {
        var root = new DirectoryInfo(Directory);
        if (!root.Exists) return true;
        // Checked here as well as by the caller: a link swapped in since then could lead anywhere.
        if (root.LinkTarget is not null) return false;

        var entries = root.EnumerateFileSystemInfos().ToList();
        var locks = entries
            .Where(e => e.LinkTarget is null)
            .SelectMany(NamesIn)
            .SelectMany(name => LockExtensions.Select(extension => File(name, extension)))
            // A daemon may be named like another's lock file; its state directory is not a lock.
            .Where(path => !System.IO.Directory.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var held = new List<FileStream>();

        try {
            foreach (var path in locks) {
                if (new FileInfo(path).LinkTarget is not null) return false;

                try {
                    // Delete sharing lets the lock file go while it is still held.
                    held.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete));
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    return false;
                }
            }

            foreach (var entry in entries) {
                if (locks.Contains(entry.FullName, StringComparer.Ordinal)) continue;

                if (entry is DirectoryInfo directory && entry.LinkTarget is null) directory.Delete(recursive: true);
                else entry.Delete();
            }

            foreach (var path in locks) System.IO.File.Delete(path);
        } finally {
            foreach (var stream in held) stream.Dispose();
        }

        if (root.EnumerateFileSystemInfos().Any()) return false;

        root.Delete();

        return true;
    }

    /// <summary>Every daemon name an entry could belong to: a directory's own name, or each dotted prefix
    /// of a file's, since a name may itself contain dots.</summary>
    static IEnumerable<string> NamesIn(FileSystemInfo entry) {
        var name = entry.Name;
        if (entry is DirectoryInfo) return [name];

        return Enumerable.Range(1, name.Length - 1).Where(i => name[i] == '.').Select(i => name[..i]);
    }

    /// <summary>Ensures the directory exists. Safe to call repeatedly.</summary>
    public void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>
    /// Returns the daemon names visible on disk — the union of names derived from the STATE markers
    /// <c>*.pid</c>, <c>*.restart-pending</c>, and <c>*.version</c>. Used by <c>daemon doctor</c> to
    /// classify held vs stale entries; covers orphan PID files (e.g. a stop that removed the lock but
    /// left the PID behind) and marker-only leftovers (a crash between queueing a restart and
    /// applying it, or a version marker left after an unclean exit).
    ///
    /// <para>A lone <c>*.lock</c> is deliberately NOT a name source. The lock file is a per-inode
    /// <c>flock</c> mutex, not a liveness marker: its mere presence never means a daemon exists (a
    /// live daemon also writes <c>.pid</c> inside the flock during startup, so it is always covered by
    /// the PID lane), and it cannot be safely deleted — unlinking a held/free <c>flock</c> file lets a
    /// later <c>daemon start</c> create a fresh inode at the same path and hold a SECOND independent
    /// flock, so <c>doctor --clean</c> deliberately leaves it. If a lone lock counted as a name, a
    /// cleaned entry (markers removed, inert lock left) would be re-listed forever — the exact bug this
    /// exclusion fixes. Once its markers are gone the name simply stops appearing; the inert lock
    /// lingers harmlessly and is reused by the next start of that name.</para>
    /// </summary>
    public IReadOnlyList<string> EnumerateNames() {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var fromPids = System.IO.Directory.EnumerateFiles(Directory, "*.pid")
            .Select(Path.GetFileNameWithoutExtension);
        var fromMarkers = System.IO.Directory.EnumerateFiles(Directory, "*.restart-pending")
            .Select(Path.GetFileNameWithoutExtension);
        var fromVersions = System.IO.Directory.EnumerateFiles(Directory, "*.version")
            .Select(Path.GetFileNameWithoutExtension);

        return [
            .. fromPids.Concat(fromMarkers).Concat(fromVersions)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct()
                .Order()
        ];
    }
}
