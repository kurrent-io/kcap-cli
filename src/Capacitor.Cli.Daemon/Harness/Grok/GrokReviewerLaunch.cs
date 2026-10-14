using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>What a hosted Grok launch is given beyond its argv: the <c>session/new</c> <c>_meta</c>, and
/// for a review launch, the environment that keeps the user's other harnesses out of it.</summary>
internal static class GrokReviewerLaunch {
    /// <summary>The review launch's tool clamp, sent as <c>_meta.agentProfile</c>: reads, search and the
    /// shell (which asks before anything it does not class as read-only), never an edit tool, a
    /// subagent or the web. Grok keeps its MCP <c>search_tool</c>/<c>use_tool</c> under any profile, which
    /// is how the review reaches its result channel.</summary>
    internal static JsonObject AgentProfile() => new() {
        ["name"]            = "kcap-reviewer",
        ["description"]     = "A read-only code reviewer that reports through its result tool.",
        ["tools"]           = new JsonArray("read_file", "grep", "list_dir", "run_terminal_cmd"),
        ["disallowedTools"] = new JsonArray("search_replace", "write", "Agent", "web_search"),
    };

    internal static JsonObject SessionNewMeta(bool isReviewFlow) {
        var meta = new JsonObject { ["yoloMode"] = false };
        if (isReviewFlow) meta["agentProfile"] = AgentProfile();
        return meta;
    }

    /// <summary>Grok loads Claude Code's and Cursor's MCP servers, hooks, skills and rules by default; a
    /// reviewer gets none of them. Subagents start in the parent's permission mode with their own tools,
    /// so they are off too. Grok's own configuration has no such switch and still applies.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ReviewerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["GROK_CLAUDE_MCPS_ENABLED"]   = "false",
        ["GROK_CLAUDE_HOOKS_ENABLED"]  = "false",
        ["GROK_CLAUDE_SKILLS_ENABLED"] = "false",
        ["GROK_CLAUDE_RULES_ENABLED"]  = "false",
        ["GROK_CLAUDE_AGENTS_ENABLED"] = "false",
        ["GROK_CURSOR_MCPS_ENABLED"]   = "false",
        ["GROK_CURSOR_HOOKS_ENABLED"]  = "false",
        ["GROK_CURSOR_SKILLS_ENABLED"] = "false",
        ["GROK_CURSOR_RULES_ENABLED"]  = "false",
        ["GROK_CURSOR_AGENTS_ENABLED"] = "false",
        ["GROK_SUBAGENTS"]             = "0",
    };
}
