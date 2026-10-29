using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>A Claude PostToolUse that moves the agent's own record of plan progress — the native task
/// tools, a subagent dispatched for a task, or a workflow ledger file such as the Superpowers
/// <c>progress.md</c> — or writes kcap plan tasks. Before the session has plan tasks these are the moments
/// it is reminded to declare them; after, a ledger line or dispatch the plan does not reflect yet is the
/// moment it is reminded to update one.</summary>
sealed partial record ClaudeTaskListEvent(string SessionId, ClaudeTaskListEvent.Moment Kind, bool FromLedger = false) {
    public enum Moment { Created, Completed, PlanWritten }

    /// <summary>A task the agent's own record moved: <see cref="Task"/> is its label there ("3" for
    /// "Task 3"), <see cref="Status"/> the kcap status it implies.</summary>
    public sealed record TaskMove(string Task, string Status);

    /// <summary>Superpowers' executing-plans and subagent-driven-development keep their ledger under this
    /// name, usually appended from the same Bash call as the commit.</summary>
    internal const string LedgerFileName = "progress.md";

    public IReadOnlyList<TaskMove> Moves { get; init; } = [];

    /// <summary>The tasks a plan write's result reported, and whether they are the plan's whole list
    /// (<c>set_plan_tasks</c>) or one changed task (<c>update_plan_task</c>).</summary>
    public IReadOnlyList<PlanTaskState> PlanTasks { get; init; } = [];
    public bool PlanTasksAreWhole { get; init; }

    /// <summary>Null for anything else, for a call that failed, and for a subagent's call: the
    /// coordinating session owns the plan, so a subagent's task list is not one to mirror. The session
    /// id comes back dashless, the form the disabled-session markers are keyed by.</summary>
    public static ClaudeTaskListEvent? Parse(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch { return null; }
        if (hook is null || Text(hook["hook_event_name"]) != "PostToolUse") return null;
        if (!string.IsNullOrEmpty(Text(hook["agent_id"])) || IsError(hook["tool_response"])) return null;

        var sessionId = Text(hook["session_id"])?.Replace("-", "");
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var tool  = Text(hook["tool_name"]);
        var input = hook["tool_input"] as JsonObject;

        if (LedgerWrite(tool, input) is { } written) {
            var moves = LedgerMoves(written);
            var kind  = moves.Any(m => m.Status == PlanTaskState.Completed) ? Moment.Completed : Moment.Created;
            return new(sessionId, kind, FromLedger: true) { Moves = moves };
        }

        if (tool == "Agent")
            return Text(input?["description"]) is { } description && DispatchedTask().Match(description) is { Success: true } m
                ? new(sessionId, Moment.Created) { Moves = [new(m.Groups[1].Value, PlanTaskState.InProgress)] }
                : null;

        if (WritesTasks(tool)) {
            var (tasks, whole) = PlanTaskState.FromResult(hook["tool_response"]);
            return new(sessionId, Moment.PlanWritten) { PlanTasks = tasks, PlanTasksAreWhole = whole };
        }

        Moment? native = tool switch {
            "TaskCreate" => Moment.Created,
            "TaskUpdate" => Text(input?["status"]) == "completed" ? Moment.Completed : null,
            "TodoWrite"  => AnyCompleted(input?["todos"]) ? Moment.Completed : Moment.Created,
            _            => null
        };

        return native is { } k ? new(sessionId, k) : null;
    }

    /// <summary>The reminder for a session with no plan tasks. Null for a dispatch, which says nothing
    /// about a plan the session never declared, and when the session id cannot be rendered safely
    /// inside a code span.</summary>
    public string? Nudge() {
        if (!Guid.TryParseExact(SessionId, "N", out _)) return null;
        if (!FromLedger && Moves.Count > 0) return null;

        var opening = (Kind, FromLedger) switch {
            (Moment.Completed, true) => "You recorded a finished task in your ledger, but this session has recorded no kcap plan, so its progress is invisible in kcap. ",
            (Moment.Completed, _)    => "You marked a task complete, but this session has recorded no kcap plan, so its progress is invisible in kcap. ",
            (_, true)                => "You are keeping a plan ledger; kcap does not see it. ",
            _                        => "You started a task list; kcap does not see it. "
        };

        var declare = FromLedger
            ? "`declare_plan_document` (kind `plan`) for the plan file your ledger's first line names, then `set_plan_tasks` with its whole ordered task list and current statuses, "
            : "`set_plan_tasks` with the whole ordered list and current statuses (after `declare_plan_document` when the tasks come from a plan, spec or design file), ";

        return opening +
            "Mirror it through the kcap-plans MCP tools: " + declare +
            $"then `update_plan_task` in the same turn as every status change you make to your own {(FromLedger ? "ledger" : "list")}. " +
            "If this session already continues a kcap plan, use `update_plan_task` on it rather than declaring another. " +
            $"Pass `session_id` (`{SessionId}`) if a tool cannot resolve the session.";
    }

    /// <summary>The moves the plan does not reflect yet, each with the plan task it names. A move whose
    /// task the session never saw in a plan result is skipped: the hook cannot tell whether it lags.</summary>
    public IReadOnlyList<(TaskMove Move, PlanTaskState Task)> Lagging(IReadOnlyList<PlanTaskState> plan) {
        var lagging = new List<(TaskMove, PlanTaskState)>();
        foreach (var move in Moves)
            if (PlanTaskState.Find(plan, move.Task) is { } task && task.Lags(move.Status))
                lagging.Add((move, task));
        return lagging;
    }

    public static string? PairingNudge(IReadOnlyList<(TaskMove Move, PlanTaskState Task)> lagging) {
        var calls = lagging
            .Where(l => PlanTaskState.IsSafeId(l.Task.TaskId))
            .Select(l => $"Task {l.Move.Task} is `{l.Task.Status}` there: call `update_plan_task` with `task_id` `{l.Task.TaskId}` and `status` `{l.Move.Status}`.")
            .ToList();
        if (calls.Count == 0) return null;

        return "Your own record moved a plan task that kcap's plan has not caught up with. " +
            string.Join(" ", calls) +
            " Pair every dispatch and ledger line with its `update_plan_task` in the same turn: `in_progress` when you start or dispatch a task, `completed` when it passes review.";
    }

    public static string RenderHookOutput(string nudge) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "PostToolUse",
                ["additionalContext"] = nudge
            }
        }.ToJsonString();

    /// <summary>The text a call wrote into a ledger file, or null when it wrote none. A Bash command counts
    /// when it redirects or tees into the file; the path before the name usually sits behind a shell
    /// variable, so only the name is matched.</summary>
    static string? LedgerWrite(string? tool, JsonObject? input) {
        switch (tool) {
            case "Bash":
                return Text(input?["command"]) is { } command && BashLedgerWrite().IsMatch(command) ? command : null;
            case "Write" or "Edit":
                if (Text(input?["file_path"]) is not { } path || !string.Equals(Path.GetFileName(path), LedgerFileName, StringComparison.OrdinalIgnoreCase)) return null;
                return Text(input?["content"]) ?? Text(input?["new_string"]) ?? "";
            default:
                return null;
        }
    }

    /// <summary>The ledger's own entry form, "Task 3: dispatched …" or "Task 3: complete …"; a bare
    /// "complete" elsewhere in a command, such as a commit message, says nothing about a task.</summary>
    static List<TaskMove> LedgerMoves(string written) =>
        LedgerEntry().Matches(written)
            .Select(m => new TaskMove(m.Groups[1].Value,
                m.Groups[2].Value.StartsWith("complete", StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value.Equals("done", StringComparison.OrdinalIgnoreCase)
                    ? PlanTaskState.Completed
                    : PlanTaskState.InProgress))
            .ToList();

    /// <summary>A declared document alone is not a ledger the user can watch: a session that declares
    /// its spec and then tracks the plan only in its own file still needs the reminder.</summary>
    static bool WritesTasks(string? tool) =>
        PlanToolNames.IsWrite(tool) && !tool!.EndsWith(PlanToolNames.DeclareDocument, StringComparison.Ordinal);

    [GeneratedRegex(@"\bTask\s+([\w.-]+)\s*:\s*(dispatched|started|in[ _-]?progress|completed?|done)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LedgerEntry();

    /// <summary>Subagent-driven development describes each implementer dispatch as "Implement Task 3: …".</summary>
    [GeneratedRegex(@"^Implement\s+Task\s+([\w.-]+?)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex DispatchedTask();

    [GeneratedRegex(@"(?:>>?|\btee\b(?:\s+-a)?)\s*[""']?[^\s;&|<>""']*progress\.md\b", RegexOptions.IgnoreCase)]
    private static partial Regex BashLedgerWrite();

    static bool IsError(JsonNode? response) =>
        response is JsonObject o && (o["isError"] ?? o["is_error"]) is JsonValue v && v.TryGetValue<bool>(out var error) && error;

    static bool AnyCompleted(JsonNode? todos) =>
        todos is JsonArray items && items.Any(t => Text(t?["status"]) == "completed");

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
