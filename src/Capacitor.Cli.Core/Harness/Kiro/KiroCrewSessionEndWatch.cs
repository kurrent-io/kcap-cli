using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Tells a Kiro session's watcher when Kiro Crew is done with it. Crew keeps one <c>kiro-cli</c>
/// process running for a whole chat and, by default, runs its sub-agents inside that same process,
/// so the process exiting — the only end signal a plain Kiro session has — can be hours away for a
/// sub-agent that finished long ago, or a chat Crew has already moved to a new session.
/// </summary>
/// <remarks>
/// A sub-agent is finished once Crew writes its <c>tombstone.json</c>; its <c>state.json</c> status is
/// not updated at the end and cannot be used. A chat's session is finished once the session map no
/// longer names it as that chat's current session. One instance watches one session and remembers
/// what it has seen between checks.
/// </remarks>
public sealed class KiroCrewSessionEndWatch(KiroCrewPaths crew, string sessionId) {
    /// <summary>Bounds each check's search for the session's own sub-agent directory; once found it is
    /// the only directory read.</summary>
    const int SubagentScanLimit = 64;

    readonly Guid? _session = Guid.TryParse(sessionId, out var id) ? id : null;

    string? _subagentDir;
    string? _chatKey;

    /// <summary>Whether Crew has finished with the session. False whenever Crew's files say nothing
    /// about it, so a plain Kiro session is never ended here.</summary>
    public bool IsFinished() {
        if (_session is not { } session || !crew.IsPresent()) return false;

        return SubagentFinished(session) || ChatMovedOn(session);
    }

    bool SubagentFinished(Guid session) {
        _subagentDir ??= FindSubagentDir(session);

        return _subagentDir is { } dir
            && KiroCrewRecords.Read(Path.Combine(dir, "tombstone.json")) is { } tombstone
            && KiroCrewRecords.GuidOf(tombstone, "session_id") == session;
    }

    string? FindSubagentDir(Guid session) {
        List<DirectoryInfo> dirs;

        try {
            dirs = new DirectoryInfo(crew.SubagentsDir).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(SubagentScanLimit)
                .ToList();
        } catch {
            return null;
        }

        foreach (var dir in dirs) {
            foreach (var file in new[] { "state.json", "tombstone.json" }) {
                if (KiroCrewRecords.Read(Path.Combine(dir.FullName, file)) is { } record
                 && KiroCrewRecords.GuidOf(record, "session_id") == session)
                    return dir.FullName;
            }
        }

        return null;
    }

    bool ChatMovedOn(Guid session) {
        if (KiroCrewRecords.Read(crew.SessionMapJson) is not { } map) return false;

        if (_chatKey is null) {
            foreach (var (key, node) in map) {
                if (node is not JsonObject entry) continue;

                if (KiroCrewRecords.GuidOf(entry, "sid") == session) {
                    _chatKey = key;

                    return false;
                }

                // Moved on before this watcher first looked.
                if (KiroCrewRecords.GuidOf(entry, "discarded_sid") == session) return true;
            }

            return false;
        }

        // The chat was dropped from the map, or now names another session.
        return map[_chatKey] is not JsonObject current
            || KiroCrewRecords.GuidOf(current, "sid") != session;
    }
}
