using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Reads Kiro's per-turn usage from a session's <c>{id}.json</c> metadata and
/// injects it onto the matching JSONL transcript line. Kiro records NO token
/// counts, but each `user_turn_metadatas[]` entry carries billing
/// <c>metering_usage[]</c> (credits) and an end-of-turn
/// <c>context_usage_percentage</c> — and that lives in the <c>.json</c>, not the
/// <c>.jsonl</c> the server normalizer sees. So the CLI maps each turn's usage to
/// its <b>final</b> message id (the turn's last entry in <c>message_ids[]</c> —
/// the final assistant response) and stamps a <c>_kcap_usage</c> object on that
/// transcript line; <c>KiroTranscriptNormalizer</c> lifts it onto
/// <c>extensions.kiro</c>. Import-only today (the live path would key off Kiro's
/// per-turn <c>stop</c> hook).
/// </summary>
public static class KiroUsage {
    public readonly record struct TurnUsage(double Credits, double? ContextPct);

    /// <summary>
    /// Builds <c>anchor message_id → usage</c> from the metadata JSON. The anchor
    /// is the turn's last <c>message_ids[]</c> entry. Empty on absent/malformed
    /// metadata or when a turn has neither credits nor a context percentage.
    /// </summary>
    public static IReadOnlyDictionary<string, TurnUsage> AnchorMap(string? metadataJson) {
        var map = new Dictionary<string, TurnUsage>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(metadataJson)) return map;

        try {
            if (JsonNode.Parse(metadataJson)?["session_state"]?["conversation_metadata"]?["user_turn_metadatas"] is not JsonArray turns) return map;

            foreach (var t in turns) {
                if (t?["message_ids"] is not JsonArray mids || mids.Count == 0) continue;
                if (mids[^1]?.GetValue<string>() is not { Length: > 0 } anchor) continue;

                double credits    = 0;
                var    hasCredits = false;

                if (t["metering_usage"] is JsonArray metering) {
                    foreach (var m in metering) {
                        if (m?["unit"]?.GetValue<string>() == "credit"
                         && m["value"] is JsonValue v && v.TryGetValue<double>(out var d)) {
                            credits    += d;
                            hasCredits =  true;
                        }
                    }
                }

                double? ctx = t["context_usage_percentage"] is JsonValue c && c.TryGetValue<double>(out var cp) ? cp : null;

                if (hasCredits || ctx is not null)
                    map[anchor] = new TurnUsage(hasCredits ? credits : 0, ctx);
            }
        } catch {
            // Malformed metadata — usage is best-effort enrichment, never fatal.
        }

        return map;
    }

    /// <summary>
    /// Returns <paramref name="line"/> with a <c>data._kcap_usage</c> object
    /// added when it is an <c>AssistantMessage</c> whose <c>message_id</c> is a
    /// turn anchor; otherwise returns it unchanged.
    /// </summary>
    public static string EnrichLine(string line, IReadOnlyDictionary<string, TurnUsage> anchors) {
        if (anchors.Count == 0) return line;

        try {
            if (JsonNode.Parse(line) is not JsonObject root           || root["kind"]?.GetValue<string>() != "AssistantMessage" || root["data"] is not JsonObject data ||
                data["message_id"]?.GetValue<string>() is not { } mid || !anchors.TryGetValue(mid, out var u)) return line;

            // bool/int/double assign to JsonObject is AOT-reflection-free; credits
            // and context% are doubles, so no JsonNode.Parse-for-strings dance.
            var usage                                                      = new JsonObject { ["credits"] = u.Credits };
            if (u.ContextPct is { } pct) usage["context_usage_percentage"] = pct;
            data["_kcap_usage"] = usage;

            return root.ToJsonString();
        } catch {
            return line;
        }
    }
}
