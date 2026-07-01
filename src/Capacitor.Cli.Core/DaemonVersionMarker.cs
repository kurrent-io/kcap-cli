namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>&lt;name&gt;.version</c> marker the daemon writes at startup (holding its
/// own version) and clears on clean exit. Read by <c>kcap daemon status</c> so it
/// can report the <b>running</b> daemon's version — letting a user confirm a
/// self-update actually took effect — without a socket round-trip.
///
/// <para>A separate, freely-readable file rather than a line in the
/// <c>&lt;name&gt;.lock</c>: the lock is held with <c>FileShare.None</c> (exclusive
/// flock), so on Windows the CLI couldn't read it at all while the daemon is live.
/// Same on-disk-marker rationale as <see cref="DaemonRestartMarker"/>.</para>
///
/// <para>A single-line plain-text file (just the version string) — no JSON, so
/// nothing to trip the NativeAOT reflection serializer.</para>
/// </summary>
public static class DaemonVersionMarker {
    public static void Write(string daemonName, string version) {
        DaemonLockPaths.EnsureDirectory();
        var path = DaemonLockPaths.VersionPath(daemonName);
        var tmp  = $"{path}.tmp";
        File.WriteAllText(tmp, version);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The recorded version, or <c>null</c> if the marker is absent, blank, or unreadable.</summary>
    public static string? TryRead(string daemonName) {
        var path = DaemonLockPaths.VersionPath(daemonName);
        if (!File.Exists(path)) return null;
        try {
            var version = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        } catch {
            return null; // unreadable — treat as absent
        }
    }

    public static void Delete(string daemonName) {
        try { File.Delete(DaemonLockPaths.VersionPath(daemonName)); } catch { /* best-effort */ }
    }
}
