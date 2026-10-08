using System.Collections.Frozen;

namespace Capacitor.App.ViewModels;

/// The kcap MCP tools in the product's words. Matching is by bare tool name: Claude prefixes the
/// server (`mcp__plugin_kcap_kcap-plans__x`), Codex records `x` alone. A prefixed name must name a
/// kcap server, so another vendor's `save_memory` is not ours.
public static class KcapToolCatalogue {
    const string McpPrefix = "mcp__";
    const string Separator = "__";
    const string PiPrefix = "kcap_";

    internal static readonly KcapToolEntry[] Entries = [
        // artefacts
        new("publish_artefact",          ToolCategory.Artefact, "Published page",           "title",  ToolCardKind.Page),
        new("set_artefact_visibility",   ToolCategory.Artefact, "Shared page",              "visibility"),
        new("await_artefact_responses",  ToolCategory.Artefact, "Waiting for answers",      null),
        new("get_artefact_results",      ToolCategory.Artefact, "Read answers",             null),
        new("close_artefact_responses",  ToolCategory.Artefact, "Closed answers",           null),
        new("list_my_artefacts",         ToolCategory.Artefact, "Listed pages",             null),
        // plans
        new("declare_plan_document",     ToolCategory.Plan,     "Declared document",        "path",   ToolCardKind.Document),
        new("set_plan_tasks",            ToolCategory.Plan,     "Set plan tasks",           null),
        new("update_plan_task",          ToolCategory.Plan,     "Updated task",             "status"),
        new("get_plan",                  ToolCategory.Plan,     "Read the plan",            null),
        // work items
        new("declare_work_item",         ToolCategory.Work,     "Attached work item",       "issue_key"),
        new("declare_loose_end",         ToolCategory.Work,     "Left a loose end",         "title"),
        new("close_loose_end",           ToolCategory.Work,     "Closed a loose end",       null),
        new("get_next_work",             ToolCategory.Work,     "Checked what to do next",  null),
        new("declare_work_breakdown",    ToolCategory.Work,     "Declared work breakdown",  null),
        new("declare_work_relation",     ToolCategory.Work,     "Declared work relation",   null),
        new("merge_work_item",           ToolCategory.Work,     "Merged work items",        null),
        new("detach_work_item",          ToolCategory.Work,     "Detached work item",       null),
        new("dismiss_next_work",         ToolCategory.Work,     "Dismissed a suggestion",   null),
        new("get_session_work_items",    ToolCategory.Work,     "Read work items",          null),
        new("request_work_item_eval",    ToolCategory.Work,     "Requested an evaluation",  null),
        // memory and knowledge
        new("save_memory",               ToolCategory.Memory,   "Saved memory",             "slug"),
        new("search_memories",           ToolCategory.Memory,   "Searched memory",          "query"),
        new("get_memory",                ToolCategory.Memory,   "Read memory",              "slug"),
        new("update_memory",             ToolCategory.Memory,   "Updated memory",           "slug"),
        new("archive_memory",            ToolCategory.Memory,   "Archived memory",          "slug"),
        new("search_facts",              ToolCategory.Memory,   "Searched facts",           "query"),
        new("list_facts",                ToolCategory.Memory,   "Listed facts",             null),
        new("get_skill",                 ToolCategory.Memory,   "Read skill",               "slug"),
        new("list_skills",               ToolCategory.Memory,   "Listed skills",            null),
        // sessions, handoff, analytics, review
        new("search_sessions",           ToolCategory.Session,  "Searched sessions",        "query"),
        new("get_session_transcript",    ToolCategory.Session,  "Read session",             "session_id"),
        new("get_session_summary",       ToolCategory.Session,  "Read session summary",     "session_id"),
        new("list_repo_sessions",        ToolCategory.Session,  "Listed sessions",          null),
        new("get_declared_plans",        ToolCategory.Session,  "Read declared plans",      null),
        new("list_repo_plans",           ToolCategory.Session,  "Listed plans",             null),
        new("get_turn",                  ToolCategory.Session,  "Read a turn",              null),
        new("list_turns",                ToolCategory.Session,  "Listed turns",             null),
        new("continue_session",          ToolCategory.Session,  "Continued session",        "session_id"),
        new("query_analytics",           ToolCategory.Session,  "Ran analytics query",      "sql"),
        new("get_analytics_schema",      ToolCategory.Session,  "Read analytics schema",    null),
        new("search_context",            ToolCategory.Session,  "Searched session context", "query"),
        new("get_pr_summary",            ToolCategory.Session,  "Read PR summary",          null),
        new("list_pr_files",             ToolCategory.Session,  "Listed PR files",          null),
        new("get_file_context",          ToolCategory.Session,  "Read file context",        "path"),
        new("get_transcript",            ToolCategory.Session,  "Read transcript",          null),
        // flows and hosted agents
        new("start_review_flow",         ToolCategory.Flow,     "Started review flow",      "kind",   ToolCardKind.Flow),
        new("start_flow",                ToolCategory.Flow,     "Started flow",             "definition_id", ToolCardKind.Flow),
        new("start_agent",               ToolCategory.Flow,     "Started hosted agent",     "prompt", ToolCardKind.Agent),
        new("submit_review_round",       ToolCategory.Flow,     "Submitted review round",   null),
        new("get_review_flow_status",    ToolCategory.Flow,     "Checked flow status",      null),
        new("get_flow_status",           ToolCategory.Flow,     "Checked flow status",      null),
        new("close_review_flow",         ToolCategory.Flow,     "Closed flow",              null),
        new("close_flow",                ToolCategory.Flow,     "Closed flow",              null),
        new("send_to_participant",       ToolCategory.Flow,     "Sent to participant",      null),
        new("list_flow_definitions",     ToolCategory.Flow,     "Listed flow options",      null),
        new("list_reviewer_vendors",     ToolCategory.Flow,     "Listed flow options",      null),
        new("list_start_agent_options",  ToolCategory.Flow,     "Listed flow options",      null),
    ];

    static readonly FrozenDictionary<string, KcapToolEntry> ByTool =
        Entries.ToFrozenDictionary(e => e.Tool, StringComparer.Ordinal);

    /// Category per kcap server, for a kcap tool the table does not list.
    static readonly FrozenDictionary<string, ToolCategory> ServerCategories = new Dictionary<string, ToolCategory>(StringComparer.Ordinal) {
        ["kcap-artefacts"] = ToolCategory.Artefact,
        ["kcap-plans"]     = ToolCategory.Plan,
        ["kcap-workitems"] = ToolCategory.Work,
        ["kcap-memory"]    = ToolCategory.Memory,
        ["kcap-knowledge"] = ToolCategory.Memory,
        ["kcap-sessions"]  = ToolCategory.Session,
        ["kcap-handoff"]   = ToolCategory.Session,
        ["kcap-analytics"] = ToolCategory.Session,
        ["kcap-review"]    = ToolCategory.Session,
        ["kcap-flows"]     = ToolCategory.Flow,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static KcapToolEntry? Match(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return null;
        var (server, tool) = Split(toolName);
        if (server is not null && !ServerCategories.ContainsKey(server)) return null;
        if (ByTool.TryGetValue(tool, out var entry)) return entry;
        if (server is not null && ServerCategories.TryGetValue(server, out var category))
            return new KcapToolEntry(tool, category, Humanise(tool), null);
        return null;
    }

    /// "Linear · create issue" for a prefixed name from another server; null for anything else.
    public static string? ForeignLabel(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return null;
        var (server, tool) = Split(toolName);
        if (server is null || server.Length == 0 || tool.Length == 0) return null;
        return $"{Humanise(server)} · {tool.Replace('_', ' ')}";
    }

    // (server, tool) for `mcp__<server>__<tool>` or Pi's `kcap_<server>_<tool>`; (null, name) for a
    // bare name. A plugin server segment (`plugin_kcap_kcap-plans`, `plugin_linear_linear`) keeps
    // its last `_`-separated token.
    static (string? Server, string Tool) Split(string name) {
        if (name.StartsWith(PiPrefix, StringComparison.Ordinal)) {
            var piRest = name[PiPrefix.Length..];
            var underscore = piRest.IndexOf('_');
            if (underscore > 0 && underscore < piRest.Length - 1) {
                var piServer = "kcap-" + piRest[..underscore];
                if (ServerCategories.ContainsKey(piServer)) return (piServer, piRest[(underscore + 1)..]);
            }
        }
        if (!name.StartsWith(McpPrefix, StringComparison.Ordinal)) return (null, name);
        var rest = name[McpPrefix.Length..];
        var cut = rest.LastIndexOf(Separator, StringComparison.Ordinal);
        if (cut <= 0) return ("", cut < 0 ? rest : rest[(cut + Separator.Length)..]);
        var server = rest[..cut];
        if (server.StartsWith("plugin_", StringComparison.Ordinal)) server = server[(server.LastIndexOf('_') + 1)..];
        return (server, rest[(cut + Separator.Length)..]);
    }

    static string Humanise(string token) {
        var words = token.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
