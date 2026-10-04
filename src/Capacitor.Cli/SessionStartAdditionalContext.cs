using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Claude-Code-specific. Joins plain-text fragments with a blank-line separator and wraps them in a
/// single SessionStart <c>hookSpecificOutput</c> envelope — one JSON object, because Claude Code parses
/// hook stdout as a single value. Returns <c>null</c> when nothing survives, so the caller writes nothing.
/// </summary>
static class SessionStartAdditionalContext {
    /// <summary>Claude Code cuts <c>additionalContext</c> beyond this many characters and the agent
    /// sees only the head, so whatever is joined last is what disappears.</summary>
    public const int MaxContextChars = 10_000;

    public static string? BuildEnvelope(params string?[] fragments) => BuildEnvelopeWithTail(fragments, trimmableTail: null);

    /// <summary>
    /// Keeps each of <paramref name="fragments"/> whole, in order, while it fits under
    /// <see cref="MaxContextChars"/>, dropping one that does not; a fragment cut mid-way could leave a
    /// data fence open. <paramref name="trimmableTail"/> goes last and is cut at a line break to fill
    /// what room is left, so a long list there cannot push the fragments ahead of it off the end.
    /// </summary>
    public static string? BuildEnvelopeWithTail(IReadOnlyList<string?> fragments, string? trimmableTail) {
        var kept = new List<string>(fragments.Count + 1);
        var used = 0;
        foreach (var f in fragments) {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var cost = Separator(kept) + f.Length;
            if (used + cost > MaxContextChars) continue;
            kept.Add(f);
            used += cost;
        }

        if (!string.IsNullOrWhiteSpace(trimmableTail)) {
            var room = MaxContextChars - used - Separator(kept);
            if (trimmableTail.Length <= room) {
                kept.Add(trimmableTail);
            } else if (room > 0) {
                var cut = trimmableTail.LastIndexOf('\n', room - 1);
                if (cut > 0) kept.Add(trimmableTail[..cut]);
            }
        }

        if (kept.Count == 0) return null;

        var envelope = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "SessionStart",
                ["additionalContext"] = string.Join("\n\n", kept)
            }
        };

        return envelope.ToJsonString();
    }

    static int Separator(List<string> kept) => kept.Count == 0 ? 0 : 2;
}
