using System.Globalization;

namespace Capacitor.Cli.Core;

/// <summary>
/// Pure heartbeat read/write + staleness policy backing the watcher wedge-detection
/// probe. Today <c>WatcherManager.IsWatcherAlive</c> only checks that
/// the PID exists, so a <b>wedged</b> watcher — main loop hung but the process still
/// alive — is never restarted; the hook self-heal only recovers dead/absent watchers.
///
/// <para>The watcher touches its own <c>{key}.heartbeat</c> file (mtime, via the
/// content written here) every main-loop iteration — including no-content drains and
/// while reconnecting — so "stale" unambiguously means the loop itself is wedged, not
/// merely idle. A hook-side staleness probe (<c>WatcherManager.IsWatcherAlive</c> /
/// <c>EnsureWatcherRunning</c>) reads it back and decides whether to kill + respawn.</para>
///
/// <para>Kept pure and side-effect-free apart from the file read/write themselves, so
/// the staleness policy (<see cref="IsStale"/>) is unit-testable without spawning any
/// process.</para>
/// </summary>
public static class WatcherHeartbeat {
    /// <summary>
    /// Startup grace: a watcher younger than this is never judged stale, even with no
    /// heartbeat yet (connecting to the hub, resolving the parent PID, etc. all take
    /// real time before the main loop's first iteration).
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Staleness threshold once past the startup grace. Generous relative to the ~1 Hz
    /// (1s) main-loop poll and the 5s disconnected-retry delay: a threshold below the
    /// SignalR reconnect backoff's 30s cap is still fine because reconnecting iterations
    /// STILL touch the heartbeat (only a genuinely wedged/hung loop stops touching it).
    /// </summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(20);

    /// <summary>Per-key heartbeat file path: <c>{watcherDir}/{key}.heartbeat</c>.</summary>
    public static string HeartbeatPath(string watcherDir, string key) =>
        Path.Combine(watcherDir, $"{key}.heartbeat");

    /// <summary>
    /// Writes <paramref name="now"/> (round-trippable ISO-8601) to <paramref name="path"/>
    /// atomically — write to a sibling temp file, then rename over the target — so a
    /// concurrent reader never observes a partially-written timestamp.
    /// </summary>
    public static void Touch(string path, DateTimeOffset now) {
        var dir = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        var tmp = $"{path}.tmp";
        File.WriteAllText(tmp, now.ToString("O", CultureInfo.InvariantCulture));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The last-written heartbeat timestamp, or <c>null</c> if absent/unreadable/unparseable.</summary>
    public static DateTimeOffset? Read(string path) {
        try {
            var text = File.ReadAllText(path).Trim();

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
        } catch {
            return null; // absent, unreadable, or racing a concurrent Touch — treat as no reading.
        }
    }

    /// <summary>
    /// Staleness policy. Within <paramref name="grace"/> of <paramref name="startupAt"/> the
    /// watcher is NEVER stale — a fresh watcher hasn't had a chance to write its first
    /// heartbeat yet, and a null <paramref name="lastBeat"/> must not read as stale during
    /// that window. Past the grace window, stale means either no heartbeat was ever recorded
    /// or the last one is older than <paramref name="threshold"/>.
    /// </summary>
    public static bool IsStale(
            DateTimeOffset? lastBeat,
            DateTimeOffset  startupAt,
            DateTimeOffset  now,
            TimeSpan        grace,
            TimeSpan        threshold
        ) {
        if (now - startupAt <= grace) {
            return false;
        }

        return lastBeat is not { } beat || (now - beat) > threshold;
    }
}
