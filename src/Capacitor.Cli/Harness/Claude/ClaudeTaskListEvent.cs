using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>A Claude PostToolUse that moves the agent's own record of plan progress — the native task
/// tools, or a workflow ledger file such as the Superpowers <c>progress.md</c> — or writes kcap plan tasks.
/// The first two are the moments a session with no kcap plan tasks is reminded to keep them; the third
/// ends the reminders for the session.</summary>
sealed partial record ClaudeTaskListEvent(string SessionId, ClaudeTaskListEvent.Moment Kind, bool FromLedger = false) {
    public enum Moment { Created, Completed, PlanWritten }

    /// <summary>Superpowers' executing-plans and subagent-driven-development keep their ledger under this
    /// name, usually appended from the same Bash call as the commit.</summary>
    internal const string LedgerFileName = "progress.md";

    /// <summary>Null for anything else, for a call that failed, and for a subagent's call: the
    /// coordinating session owns the plan, so a subagent's task list is not one to mirror. The session id comes back dashless, the
    /// form the disabled-session markers are keyed by.</summary>
    public static ClaudeTaskListEvent? Parse(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch { return null; }
        if (hook is null || Text(hook["hook_event_name"]) != "PostToolUse") return null;
        if (!string.IsNullOrEmpty(Text(hook["agent_id"])) || IsError(hook["tool_response"])) return null;

        var sessionId = Text(hook["session_id"])?.Replace("-", "");
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var tool  = Text(hook["tool_name"]);
        var input = hook["tool_input"] as JsonObject;

        if (LedgerWrite(tool, input) is { } written)
            return new(sessionId, FinishedEntry().IsMatch(written) ? Moment.Completed : Moment.Created, FromLedger: true);

        Moment? kind = tool switch {
            "TaskCreate"                       => Moment.Created,
            "TaskUpdate"                       => Text(input?["status"]) == "completed" ? Moment.Completed : null,
            "TodoWrite"                        => AnyCompleted(input?["todos"]) ? Moment.Completed : Moment.Created,
            _ when WritesTasks(tool)           => Moment.PlanWritten,
            _                                  => null
        };

        return kind is { } k ? new(sessionId, k) : null;
    }

    /// <summary>Null when the session id cannot be rendered safely inside a code span.</summary>
    public string? Nudge() {
        if (!Guid.TryParseExact(SessionId, "N", out _)) return null;

        var opening = (Kind, FromLedger) switch {
            (Moment.Completed, true) => "You recorded a finished task in your ledger, but this session has recorded no kcap plan, so its progress is invisible in Capacitor. ",
            (Moment.Completed, _)    => "You marked a task complete, but this session has recorded no kcap plan, so its progress is invisible in Capacitor. ",
            (_, true)                => "You are keeping a plan ledger; Capacitor does not see it. ",
            _                        => "You started a task list; Capacitor does not see it. "
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

    /// <summary>A declared document alone is not a ledger the user can watch: a session that declares
    /// its spec and then tracks the plan only in its own file still needs the reminder.</summary>
    static bool WritesTasks(string? tool) =>
        PlanToolNames.IsWrite(tool) && !tool!.EndsWith(PlanToolNames.DeclareDocument, StringComparison.Ordinal);

    /// <summary>The Superpowers ledger's completion line; a bare "complete" elsewhere in a command, such
    /// as a commit message, says nothing about a task.</summary>
    [GeneratedRegex(@"\bTask\s+[\w.-]+\s*:\s*complete(?:d)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinishedEntry();

    [GeneratedRegex(@"(?:>>?|\btee\b(?:\s+-a)?)\s*[""']?[^\s;&|<>""']*progress\.md\b", RegexOptions.IgnoreCase)]
    private static partial Regex BashLedgerWrite();

    static bool IsError(JsonNode? response) =>
        response is JsonObject o && (o["isError"] ?? o["is_error"]) is JsonValue v && v.TryGetValue<bool>(out var error) && error;

    static bool AnyCompleted(JsonNode? todos) =>
        todos is JsonArray items && items.Any(t => Text(t?["status"]) == "completed");

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
