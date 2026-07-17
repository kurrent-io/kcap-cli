using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>
/// AI-1382 D0/D1 — per-session on-disk marker paths for the Cursor tailing watcher's runtime
/// rewrite guard (quarantine — read/write logic lives here, this task), ordering-sensitive hook
/// side-effect barrier, and heartbeat (the latter two are introduced as PATH helpers only; Task 8
/// wires the barrier's create/clear logic and the heartbeat touch/read calls against
/// <see cref="WatcherHeartbeat"/>). One dot-namespaced directory per marker kind under the CLI's
/// config dir (honouring <c>KCAP_CONFIG_DIR</c> via <see cref="PathHelpers.ConfigPath"/>), one
/// file per session keyed by the dashless session id, so every process on this machine — hook,
/// watcher, backfill, import — resolves the same path.
/// </summary>
public static class CursorMarkers {
    public static string QuarantinePath(string sessionId) => Path.Combine(PathHelpers.ConfigPath("cursor-quarantine"), $"{sessionId}.json");
    public static string BarrierPath(string sessionId)    => Path.Combine(PathHelpers.ConfigPath("cursor-barrier"), $"{sessionId}.json");
    public static string HeartbeatPath(string sessionId)  => Path.Combine(PathHelpers.ConfigPath("cursor-heartbeat"), $"{sessionId}.json");

    /// <summary>
    /// True once <see cref="Quarantine"/> has written a marker for this session. Fail-open on any
    /// read error (corrupt/partial marker, permission issue) — a broken quarantine check must
    /// never itself become the reason live capture stalls. Consulted by the backfill and import
    /// source in a later task.
    /// </summary>
    public static bool IsQuarantined(string sessionId) {
        try { return File.Exists(QuarantinePath(sessionId)); } catch { return false; }
    }

    /// <summary>
    /// Writes a timestamped quarantine marker for <paramref name="sessionId"/>, atomically
    /// (sibling temp file + rename — the <see cref="WatcherHeartbeat.Touch"/> precedent) so a
    /// concurrent reader never observes a partial write. Idempotent — a session quarantined twice
    /// keeps the FIRST reason/timestamp (the first detection is the useful diagnostic; a later
    /// trip is corroborating, not new information) unless the existing marker is corrupt or
    /// unreadable, in which case it's replaced.
    /// </summary>
    public static void Quarantine(string sessionId, string reason) {
        if (ReadMarker(sessionId) is not null) return; // already quarantined; keep the first reason

        var path = QuarantinePath(sessionId);
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var marker = new CursorQuarantineMarker(reason, DateTimeOffset.UtcNow);
        var json   = JsonSerializer.Serialize(marker, CapacitorJsonContext.Default.CursorQuarantineMarker);
        var tmp    = $"{path}.tmp";

        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The persisted quarantine marker for this session, or null if absent/corrupt.</summary>
    public static CursorQuarantineMarker? ReadMarker(string sessionId) {
        var path = QuarantinePath(sessionId);
        if (!File.Exists(path)) return null;

        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CursorQuarantineMarker);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // Corrupt/partial JSON, or a transient read error while a peer holds the file
            // exclusively mid-write — treat as "not quarantined yet" (MachineId.ReadPersisted
            // precedent), never throw out of a guard check.
            return null;
        }
    }
}

/// <summary>Quarantine marker persisted to <see cref="CursorMarkers.QuarantinePath"/>.</summary>
public readonly record struct CursorQuarantineMarker(string Reason, DateTimeOffset QuarantinedAt);
