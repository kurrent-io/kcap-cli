using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Claude-Code-specific. Joins zero or more plain-text fragments (produced
/// by vendor-neutral or Claude-specific contributors — e.g. the recurring
/// lessons emitter, the upgrade-nudge emitter) with a blank-line separator
/// and wraps them in a single Claude Code SessionStart
/// <c>hookSpecificOutput</c> envelope. Returns <c>null</c> when every
/// fragment is null/empty/whitespace so the caller writes nothing at all
/// to stdout.
///
/// One call site emits one JSON object — Claude Code hooks parse stdout
/// as a single JSON value with plain-text fallback, so two top-level
/// envelopes would not be parsed.
/// </summary>
static class SessionStartAdditionalContext {
    /// <summary>
    /// Joins the non-null, non-whitespace <paramref name="fragments"/> with a
    /// blank-line separator and returns the JSON-serialized SessionStart
    /// <c>hookSpecificOutput</c> envelope. Returns <c>null</c> when no fragment
    /// survives the filter, so the caller can skip writing to stdout entirely.
    /// </summary>
    public static string? BuildEnvelope(params string?[] fragments) {
        if (fragments.Length == 0) return null;

        var kept = new List<string>(fragments.Length);
        foreach (var f in fragments) {
            if (!string.IsNullOrWhiteSpace(f)) kept.Add(f);
        }

        if (kept.Count == 0) return null;

        var ctx = string.Join("\n\n", kept);

        var envelope = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "SessionStart",
                ["additionalContext"] = ctx
            }
        };

        return envelope.ToJsonString();
    }
}
