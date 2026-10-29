using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>One plan task as a <c>kcap-plans</c> write last reported it — the session's local view of
/// the plan, read back to tell whether a ledger line or dispatch has been mirrored yet.</summary>
sealed record PlanTaskState(string TaskId, int Ordinal, string? Title, string Status) {
    public const string InProgress = "in_progress";
    public const string Completed  = "completed";

    /// <summary>Whether the plan is behind a move to <paramref name="target"/>. A skipped task is
    /// settled, and a task already past <c>in_progress</c> is not behind a dispatch.</summary>
    public bool Lags(string target) => target switch {
        Completed  => Status is not (Completed or "skipped"),
        InProgress => Status == "pending",
        _          => false
    };

    /// <summary>The task a ledger label names: by title first, since plans usually title their tasks
    /// "Task 3: …", then by ordinal.</summary>
    public static PlanTaskState? Find(IReadOnlyList<PlanTaskState> plan, string label) {
        var prefix = $"Task {label}";
        var byTitle = plan.FirstOrDefault(t => t.Title is { } title
            && title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (title.Length == prefix.Length || !char.IsLetterOrDigit(title[prefix.Length])));
        if (byTitle is not null) return byTitle;
        return int.TryParse(label, out var ordinal) ? plan.FirstOrDefault(t => t.Ordinal == ordinal) : null;
    }

    /// <summary>Rendered inside a code span, so anything but a plain id suppresses the reminder.</summary>
    public static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>The tasks in a <c>set_plan_tasks</c> or <c>update_plan_task</c> result, which Claude hands
    /// the hook as MCP content blocks whose text is the server's JSON answer.</summary>
    public static (IReadOnlyList<PlanTaskState> Tasks, bool Whole) FromResult(JsonNode? response) {
        foreach (var answer in Answers(response)) {
            if (answer["tasks"] is JsonArray all) return (all.Select(From).OfType<PlanTaskState>().ToList(), true);
            if (answer["task"] is JsonObject one && From(one) is { } task) return ([task], false);
        }
        return ([], false);
    }

    public JsonObject ToJson() =>
        new() { ["task_id"] = TaskId, ["ordinal"] = Ordinal, ["title"] = Title, ["status"] = Status };

    public static PlanTaskState? From(JsonNode? node) =>
        node is JsonObject o
     && Text(o["task_id"]) is { Length: > 0 } id
     && Text(o["status"]) is { Length: > 0 } status
     && o["ordinal"] is JsonValue ordinal && ordinal.TryGetValue<int>(out var n)
            ? new PlanTaskState(id, n, Text(o["title"]), status)
            : null;

    static IEnumerable<JsonObject> Answers(JsonNode? response) {
        switch (response) {
            case JsonObject { } o when o["tasks"] is not null || o["task"] is not null:
                yield return o;
                break;
            case JsonObject { } o when o["content"] is JsonArray blocks:
                foreach (var a in FromBlocks(blocks)) yield return a;
                break;
            case JsonArray blocks:
                foreach (var a in FromBlocks(blocks)) yield return a;
                break;
            case JsonValue v when v.TryGetValue<string>(out var text) && Parse(text) is { } parsed:
                yield return parsed;
                break;
        }
    }

    static IEnumerable<JsonObject> FromBlocks(JsonArray blocks) {
        foreach (var block in blocks)
            if (Text(block?["text"]) is { } text && Parse(text) is { } parsed)
                yield return parsed;
    }

    static JsonObject? Parse(string text) {
        try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
