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

    const string Separator = "\n\n";

    public static string? BuildEnvelope(params string?[] fragments) =>
        BuildRankedEnvelope(fragments.Select(f => new ContextFragment(f)).ToList());

    /// <summary>
    /// Renders <paramref name="fragments"/> in the order given, but hands out room under
    /// <see cref="MaxContextChars"/> by rank, lowest first, so a long fragment cannot crowd out a shorter
    /// better-ranked one placed after it. A fragment that does not fit is cut at a line break when it is
    /// trimmable and dropped otherwise: cutting anything else could leave a data fence open.
    /// </summary>
    public static string? BuildRankedEnvelope(IReadOnlyList<ContextFragment> fragments) {
        var granted = new string?[fragments.Count];
        // Every kept fragment is charged a separator, so the budget carries one extra for the first.
        var room = MaxContextChars + Separator.Length;

        foreach (var i in Enumerable.Range(0, fragments.Count).OrderBy(i => fragments[i].Rank)) {
            var (text, _, trimmable) = fragments[i];
            if (string.IsNullOrWhiteSpace(text)) continue;
            var fit = text.Length + Separator.Length <= room ? text
                    : trimmable ? CutAtLineBreak(text, room - Separator.Length)
                    : null;
            if (fit is null) continue;
            granted[i] = fit;
            room -= fit.Length + Separator.Length;
        }

        var kept = granted.OfType<string>().ToList();
        if (kept.Count == 0) return null;

        var envelope = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "SessionStart",
                ["additionalContext"] = string.Join(Separator, kept)
            }
        };

        return envelope.ToJsonString();
    }

    static string? CutAtLineBreak(string text, int maxChars) {
        if (maxChars <= 0) return null;
        var cut = text.LastIndexOf('\n', maxChars - 1);
        return cut > 0 ? text[..cut] : null;
    }
}
