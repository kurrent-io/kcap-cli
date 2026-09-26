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
public sealed class KiroCrewSessionEndWatch(KiroCrewPaths crew, string sessionsDir, string sessionId, TimeProvider time) {
    /// <summary>Crew creates a sub-agent's directory as it spawns the sub-agent, just before its session
    /// starts, so only directories touched since shortly before the session was created are searched,
    /// however many other sub-agents a busy Crew has and however late this watch began.</summary>
    static readonly TimeSpan SubagentRecency = TimeSpan.FromMinutes(10);

    DateTime? _searchFrom;

    /// <summary>Crew records a sub-agent within seconds of spawning it, so a session not found as one
    /// within this many checks is not one and stops being searched for.</summary>
    const int SubagentSearchChecks = 12;

    /// <summary>A chat entry that is missing or has no readable session, rather than naming another one,
    /// has to stay that way this many readable checks running before it counts: Crew rewrites the map
    /// in place.</summary>
    const int AbsentChatChecks = 3;

    readonly Guid? _session = Guid.TryParse(sessionId, out var id) ? id : null;

    string? _subagentDir;
    int     _subagentSearches;
    string? _chatKey;
    int     _absentChat;

    /// <summary>Whether Crew has finished with the session. False whenever Crew's files say nothing
    /// about it, so a plain Kiro session is never ended here.</summary>
    public bool IsFinished() {
        if (_session is not { } session || !crew.IsPresent()) return false;

        return ChatMovedOn(session) || SubagentFinished(session);
    }

    bool SubagentFinished(Guid session) {
        if (_chatKey is not null) return false;

        if (_subagentDir is null && _subagentSearches < SubagentSearchChecks) {
            _subagentSearches++;
            _subagentDir = FindSubagentDir(session);
        }

        return _subagentDir is { } dir
            && KiroCrewRecords.Read(Path.Combine(dir, "tombstone.json")) is { } tombstone
            && KiroCrewRecords.GuidOf(tombstone, "session_id") == session;
    }

    string? FindSubagentDir(Guid session) {
        List<DirectoryInfo> dirs;

        try {
            _searchFrom ??= ((KiroCrewRecords.SessionCreatedAt(sessionsDir, session) ?? time.GetUtcNow()) - SubagentRecency).UtcDateTime;

            dirs = new DirectoryInfo(crew.SubagentsDir).EnumerateDirectories()
                .Where(d => d.LastWriteTimeUtc >= _searchFrom)
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
        if (KiroCrewRecords.Read(crew.SessionMapJson) is not { } map) {
            _absentChat = 0;

            return false;
        }

        var discarded = false;

        foreach (var (key, node) in map) {
            if (node is not JsonObject entry) continue;

            // Current in any chat means live, even if the chat it was first seen in moved on.
            if (KiroCrewRecords.GuidOf(entry, "sid") == session) {
                _chatKey    = key;
                _absentChat = 0;

                return false;
            }

            discarded |= KiroCrewRecords.GuidOf(entry, "discarded_sid") == session;
        }

        // Never seen as current: replaced before this watcher first looked.
        if (_chatKey is null) return discarded;

        if (map[_chatKey] is JsonObject current && KiroCrewRecords.GuidOf(current, "sid") is not null) {
            _absentChat = 0;

            return true;
        }

        return ++_absentChat >= AbsentChatChecks;
    }
}
