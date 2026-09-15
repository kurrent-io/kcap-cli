using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>
/// The SessionStart "plans" nudge: two sentences telling the agent to declare the plan document and
/// task list it works from through the kcap-plans MCP tools. Like the work-items nudge it is a pure
/// function of the session id, composed at the output layer after the lease-gated fragments are
/// decided, and gated on the server being materialized in the invoking harness's config.
/// </summary>
static class PlansNudgeEmitter {
    /// <summary>Upper bound on the rendered session id, so a malformed hook payload can't inject an
    /// unbounded string.</summary>
    const int MaxSessionIdLength = 256;

    /// <summary>
    /// Resolves the nudge fragment for a harness: <c>null</c> (emit nothing) when the user opted out
    /// (<c>disable_plans_nudge</c>), when <c>kcap-plans</c> is not materialized in that harness's
    /// config, or when there is no usable session id; otherwise the built nudge.
    /// <paramref name="codexConfigPath"/> is the availability gate's test seam and is null in production.
    /// </summary>
    public static string? Resolve(HarnessId harness, string? sessionId, bool optedOut,
                                  HarnessRegistry harnesses, string? codexConfigPath = null) {
        if (optedOut) return null;
        if (!McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-plans", codexConfigPath)) return null;
        return Build(sessionId);
    }

    public static string? Build(string? sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var id = sessionId.Trim();
        if (id.Length > MaxSessionIdLength) return null;
        // Rendered verbatim inside a code span: a backtick or control char would break the span or
        // smuggle formatting, so such an id suppresses the nudge instead.
        foreach (var c in id)
            if (c == '`' || char.IsControl(c)) return null;

        return
            "## Plans\n" +
            "Plans and tasks are declared through the kcap-plans MCP tools: when you write or are handed a " +
            "plan, spec or design document, declare it with `declare_plan_document`; when a plan has discrete " +
            "steps, declare them with `set_plan_tasks` and record every status change with `update_plan_task`. " +
            $"After compaction `get_plan` returns the list, and if a tool cannot resolve the session, pass `session_id` (`{id}`) explicitly.";
    }
}
