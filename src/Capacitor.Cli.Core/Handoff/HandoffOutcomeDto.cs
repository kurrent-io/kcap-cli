using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Continuation;

namespace Capacitor.Cli.Core.Handoff;

/// <summary>What a hand-off's takeover carried, item by item. State is running, refused, failed,
/// unauthorized, done or interrupted; interrupted is only for a takeover a daemon restart cut short,
/// so <see cref="From"/> never produces it.</summary>
public sealed record HandoffOutcomeDto(
    string SourceSessionId,
    string? SourceTitle,
    string State,
    string? Detail,
    IReadOnlyList<HandoffOutcomeItemDto> Items
) {
    public static HandoffOutcomeDto Running(string sourceSessionId, string? sourceTitle) =>
        new(sourceSessionId, sourceTitle, "running", null, []);

    /// <summary>The server refused the hand-off before the takeover ran.</summary>
    public static HandoffOutcomeDto Refused(string sourceSessionId, string? sourceTitle, string code) =>
        new(sourceSessionId, sourceTitle, "refused", code, []);

    public static HandoffOutcomeDto From(string sourceSessionId, string? sourceTitle, TakeoverResult result) =>
        result switch {
            TakeoverResult.Refused r     => new(sourceSessionId, sourceTitle, "refused", r.Reason, []),
            TakeoverResult.Unauthorized  => new(sourceSessionId, sourceTitle, "unauthorized", null, []),
            TakeoverResult.Failed f      => new(sourceSessionId, sourceTitle, "failed", f.Reason, []),
            TakeoverResult.Interrupted i => new(sourceSessionId, sourceTitle, "failed", i.Code, Itemise(i.Outcome)),
            TakeoverResult.Completed c   => new(sourceSessionId, sourceTitle, c.CredentialRejected ? "unauthorized" : "done", null, Itemise(c.Outcome)),
            _                            => throw new InvalidOperationException(result.GetType().Name),
        };

    static List<HandoffOutcomeItemDto> Itemise(JsonObject outcome) {
        var items = new List<HandoffOutcomeItemDto>();

        if (outcome["work_items"] is JsonObject workItems) {
            switch (Str(workItems["status"])) {
                case "not_in_plan":
                    items.Add(new("work_item", "Work items", "skipped", "not_in_plan"));
                    break;
                case "failed":
                    items.Add(new("work_item", "Work items", "failed", Str(workItems["error"])));
                    break;
                default:
                    foreach (var entry in (workItems["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        items.Add(Write("work_item", Str(entry["label"]) ?? Str(entry["work_item_id"]) ?? "", entry));
                    break;
            }
        }

        if (Str(outcome["plans_error"]) is { } plansError)
            items.Add(new("plans", "Plans", "failed", plansError));

        foreach (var plan in (outcome["plans"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            items.Add(Write("plan", Str(plan["title"]) ?? Str(plan["plan_id"]) ?? "", plan));

        foreach (var plan in (outcome["skipped_plans"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            items.Add(new("plan", Str(plan["plan_id"]) ?? "", "skipped", Str(plan["reason"])));

        if (outcome["plans_truncated"] is JsonValue truncated && truncated.TryGetValue(out bool isTruncated) && isTruncated)
            items.Add(new("plans", "Plans", "unknown",
                string.Create(CultureInfo.InvariantCulture, $"only the {SessionTakeover.PlansReadCap} most recently touched plans were checked")));

        if (outcome["loose_end_claims"] is JsonObject claims && Claims(claims) is { } claimsItem) items.Add(claimsItem);

        return items;
    }

    static HandoffOutcomeItemDto Write(string kind, string label, JsonObject entry) =>
        entry["attached"] is JsonValue attached && attached.TryGetValue(out bool ok)
            ? ok ? new(kind, label, "carried", null) : new(kind, label, "failed", Str(entry["error"]))
            : new(kind, label, "unknown", null);

    /// <summary>Nothing to adopt is not worth a row: a claims row appears only when something was at stake.</summary>
    static HandoffOutcomeItemDto? Claims(JsonObject claims) {
        const string label = "Loose-end claims";

        return Str(claims["status"]) switch {
            "ok" when claims["results"] is JsonArray { Count: > 0 } => new("claims", label, "carried", null),
            "ok"                                                     => null,
            "partial"                                                => new("claims", label, "refused", null),
            "unsupported" or "unavailable"                           => new("claims", label, "skipped", Str(claims["status"])),
            "failed"                                                 => new("claims", label, "failed", Str(claims["error"])),
            _                                                        => new("claims", label, "unknown", null),
        };
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
