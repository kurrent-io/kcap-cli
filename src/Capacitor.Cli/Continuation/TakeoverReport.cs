using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

/// <summary>The takeover outcome as Markdown, printed by <c>kcap recap --continue</c> ahead of the recap.</summary>
static class TakeoverReport {
    public static string Render(JsonObject outcome) {
        var sb = new StringBuilder();
        sb.AppendLine($"## Continued from session {Str(outcome["continued_from"])}");
        sb.AppendLine();

        var workItems = outcome["work_items"];
        switch (Str(workItems?["status"])) {
            case "not_in_plan":
                sb.AppendLine("Work items: not available on this plan.");
                break;
            case "failed":
                sb.AppendLine($"Work items: could not be read ({Str(workItems?["error"])}).");
                break;
            default:
                var items = workItems?["items"] as JsonArray;
                sb.AppendLine(items is { Count: > 0 } ? "Work items:" : "Work items: none.");
                foreach (var item in items ?? new JsonArray())
                    sb.AppendLine(IsTrue(item?["attached"])
                        ? $"- {Str(item?["label"])}"
                        : $"- {Str(item?["label"])} — not attached: {Str(item?["error"])}");
                break;
        }

        sb.AppendLine();

        if (Str(outcome["plans_error"]) is { } plansError) {
            sb.AppendLine($"Plans: could not be read ({plansError}).");
        } else {
            var plans   = outcome["plans"] as JsonArray ?? new JsonArray();
            var skipped = outcome["skipped_plans"] as JsonArray ?? new JsonArray();
            sb.AppendLine(plans.Count + skipped.Count > 0 ? "Plans:" : "Plans: none.");

            foreach (var plan in plans) {
                var line = $"- {Str(plan?["plan_id"])}: task {Str(plan?["task_id"])} \"{Str(plan?["title"])}\" ({Str(plan?["status"])})";
                sb.AppendLine(IsTrue(plan?["attached"]) ? line : $"{line} — not attached: {Str(plan?["error"])}");
            }

            foreach (var plan in skipped)
                sb.AppendLine($"- {Str(plan?["plan_id"])} — skipped: {SkipReason(Str(plan?["reason"]))}");

            if (IsTrue(outcome["plans_truncated"]))
                sb.AppendLine($"Only the {SessionTakeover.PlansReadCap} most recently touched plans were checked.");
        }

        if (Str(outcome["current_plan_id"]) is { } current) {
            sb.AppendLine();
            sb.AppendLine($"Current plan: {current}");
        }

        return sb.ToString();
    }

    static string? SkipReason(string? reason) => reason switch {
        "not_adoptable" => "no open task this session can take over (set by the user, or by a session you cannot see)",
        _               => reason?.Replace('_', ' '),
    };

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
