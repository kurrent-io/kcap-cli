using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// The file changes validate-plan lists for a session: the server's recap rows, each marked failed when a failed
/// tool result names it. A failed call the recap holds no row for is listed on its own, so a failed attempt
/// neither disappears nor reads as done.
static class PlanWorkEvidence {
    static readonly FrozenDictionary<string, string> RecapKinds = new Dictionary<string, string> {
        ["write"] = "Write", ["edit"] = "Edit", ["delete"] = "Delete",
    }.ToFrozenDictionary();

    static readonly (string Header, string Kind)[] PatchHeaders = [
        ("*** Add File:", "Write"), ("*** Update File:", "Edit"), ("*** Delete File:", "Delete"),
    ];

    public static IReadOnlyList<PlanWorkRow> Collect(IReadOnlyList<RecapEntry> entries, IReadOnlyList<ErrorEntry> failures, string sessionId) {
        var own = OwnSession(entries, sessionId);

        var rows = entries
            .Where(e => own(e.SessionId) && RecapKinds.ContainsKey(e.Type))
            .Select(e => new PlanWorkRow(RecapKinds[e.Type], e.FilePath, e.AgentId, e.Timestamp, Failed: false))
            .ToList();

        foreach (var failure in failures.Where(f => own(f.SessionId)).OrderBy(f => f.Timestamp)) {
            foreach (var (kind, path, recorded) in FailedChanges(failure)) {
                var match = recorded
                    ? rows.FindLastIndex(r => !r.Failed && r.Kind == kind && r.AgentId == failure.AgentId
                                           && r.Timestamp <= failure.Timestamp && SamePath(r.Path, path))
                    : -1;

                if (match < 0) {
                    rows.Add(new PlanWorkRow(kind, path, failure.AgentId, failure.Timestamp, Failed: true));
                    continue;
                }

                // One call can project several rows for one file (Pi's edit, one per replacement).
                var call = rows[match];
                for (var i = 0; i < rows.Count; i++) {
                    if (rows[i] is { Failed: false } r && r.Kind == call.Kind && r.AgentId == call.AgentId
                     && r.Timestamp == call.Timestamp && SamePath(r.Path, call.Path))
                        rows[i] = r with { Failed = true };
                }
            }
        }

        return rows.OrderBy(r => r.Timestamp).ToList();
    }

    /// Matches the session in any spelling of its id: the server reports a GUID in its 32-hex form. A slug names a
    /// meta-session, which no row carries, so then every row the server returned belongs to it.
    static Func<string?, bool> OwnSession(IReadOnlyList<RecapEntry> entries, string sessionId) =>
        Guid.TryParse(sessionId, out _) || entries.Any(e => SessionIds.SameSession(e.SessionId, sessionId))
            ? id => SessionIds.SameSession(id, sessionId)
            : _ => true;

    /// What a failed call tried to change, and whether the recap projects that call as a row the failure must mark
    /// rather than add.
    static IEnumerable<(string Kind, string? Path, bool Recorded)> FailedChanges(ErrorEntry failure) {
        var input = failure.ToolInput ?? default;

        switch (failure.ToolName) {
            case "Write" or "write":
                return [("Write", FilePath(input), true)];
            case "write_file":
                return [("Write", FilePath(input), false)];
            case "Edit":
                return [("Edit", FilePath(input), true)];
            // Pi's edit carries an `edits` array, the one lowercase edit shape the recap projects.
            case "edit":
                return [("Edit", FilePath(input), input.Arr("edits") is not null)];
            case "MultiEdit" or "NotebookEdit" or "replace" or "search_replace":
                return [("Edit", FilePath(input), false)];
            case "apply_patch" or "patch":
                return PatchChanges(input, recorded: failure.ToolName == "apply_patch");
            default:
                return [];
        }
    }

    static string? FilePath(JsonElement input) =>
        input.Str("file_path") ?? input.Str("filePath") ?? input.Str("path") ?? input.Str("notebook_path");

    static List<(string Kind, string? Path, bool Recorded)> PatchChanges(JsonElement input, bool recorded) {
        var text = input.IsString
            ? input.GetString()
            : input.Str("input") ?? input.Str("command") ?? input.Str("patch") ?? input.Str("patchText");

        var changes = new List<(string Kind, string? Path, bool Recorded)>();

        foreach (var raw in (text ?? "").Split('\n')) {
            var line = raw.Trim();

            foreach (var (header, kind) in PatchHeaders) {
                if (!line.StartsWith(header, StringComparison.Ordinal)) continue;

                var path = line[header.Length..].Trim();
                if (path.Length > 0) changes.Add((kind, path, recorded));

                break;
            }
        }

        // A failed patch whose files cannot be read is still a failed edit.
        if (changes.Count == 0) changes.Add(("Edit", null, false));

        return changes;
    }

    /// The recap and a failure can spell one file differently: one absolute, the other workspace-relative.
    static bool SamePath(string? a, string? b) {
        if (a is null || b is null) return false;

        a = a.Replace('\\', '/');
        b = b.Replace('\\', '/');

        return a == b || a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal);
    }
}
