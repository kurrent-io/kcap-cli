namespace Capacitor.Cli.Core.Mcp;

/// <summary>One kcap MCP server, described semantically (no harness field names).
/// <paramref name="AutoApprove"/> marks a server safe to run without a per-call prompt where the
/// harness has a per-server trust knob (see <see cref="McpConfigShape.Trust"/>): every tool either
/// reads, or writes only to the user's own Capacitor workspace, the destination the session hooks
/// already post to unprompted. kcap-flows launches a paid hosted agent and kcap-artefacts can widen
/// who may open a page, so both keep prompting.</summary>
public sealed record KcapMcpServer(string Name, string[] Args, bool NeedsProjectCwd, string? Description, bool AutoApprove = false);

/// <summary>The single source of truth for the kcap MCP servers. Every writer
/// (Codex TOML, the JSON harnesses, the bundled `.mcp.json`) derives from this.</summary>
public static class KcapMcpServers {
    public const string Command = "kcap";

    /// <summary>The one server whose registration carries a driver stamp (see <see cref="ForHarness"/>).</summary>
    internal const string FlowsServerName = "kcap-flows";

    /// <summary>The flag that stamps the driving harness's vendor into the flows registration.</summary>
    internal const string DriverArg = "--driver";

    public static readonly IReadOnlyList<KcapMcpServer> All = [
        new("kcap-review",   ["mcp", "review"],   NeedsProjectCwd: false,
            "PR review context tools — query implementation session transcripts.", AutoApprove: true),
        new("kcap-sessions", ["mcp", "sessions"], NeedsProjectCwd: true,
            "Search and recall past Kurrent Capacitor sessions — the reasoning behind prior work (why / what-was-tried / who-decided). Repo-aware; reach for it before git log or grep for history questions.", AutoApprove: true),
        new("kcap-flows",    ["mcp", "flows"],    NeedsProjectCwd: true,
            "Structured AI agent flows — launches a SEPARATE hosted participant agent; requires login + a running daemon."),
        new("kcap-memory",   ["mcp", "memory"],   NeedsProjectCwd: true,
            "Team memory — search, read, and save durable learnings.", AutoApprove: true),
        new("kcap-workitems", ["mcp", "workitems"], NeedsProjectCwd: true,
            "Attach the current session to a work item (issue, PR, or a brand-new item), and list what a session is attached to.", AutoApprove: true),
        new("kcap-plans", ["mcp", "plans"], NeedsProjectCwd: true,
            "Declare the plan, spec or design document a session works from and the plan's task list; update task status and read the plan back after compaction.", AutoApprove: true),
        new("kcap-artefacts", ["mcp", "artefacts"], NeedsProjectCwd: true,
            "Publish a self-contained HTML page — a plan, a report, a comparison — and get back a link to share. Sandboxed with no network access, so everything is inlined; private until you set visibility."),
        new("kcap-analytics", ["mcp", "analytics"], NeedsProjectCwd: true,
            "Query the org's AI coding-agent analytics (sessions, tools, tokens, cost, commits, PRs, evals) with read-only SQL. Repo-aware: defaults to the current repo; pass scope 'global' for org-wide.", AutoApprove: true),
    ];

    /// <summary>Codex receives the full set. Kept as a named per-harness seam so a future
    /// divergence has a home, but today it is the whole `All` list — `kcap-workitems` is now
    /// registered everywhere (its session id resolves from an explicit arg / `CLAUDE_CODE_SESSION_ID` / `KCAP_SESSION_ID` /
    /// `CODEX_THREAD_ID`, and its breakdown/relation tools need no session id at all). Flows is
    /// never auto-approved.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCodex => All;

    /// <summary>The bare (pre-stamp) set for every non-Claude JSON harness (Cursor, Copilot,
    /// OpenCode, Kiro, Gemini, Antigravity) — the full `All` list, `kcap-workitems` included.
    /// <see cref="ForHarness"/> derives each harness's actual registration from this by stamping
    /// the flows entry with that harness's driver vendor.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCursor => All;

    /// <summary>The server set one JSON harness registers, with its <c>kcap-flows</c> entry stamped
    /// <c>--driver &lt;vendor&gt;</c>. The stamp is the ONLY per-process signal for the driving harness's
    /// identity on the six JSON harnesses, which — unlike Claude Code and Codex — export no distinctive
    /// env var into the long-lived MCP server child (see <c>DriverVendor</c> / <c>HarnessRequesterContext</c>).
    /// The extra argv reaches the SAME <c>kcap mcp flows</c> subcommand and therefore the same tool
    /// schema; it only tells the server which vendor is driving, so a reviewer can be recommended that
    /// differs from it. Claude/Codex stay on env inference (unstamped), so their registrations are
    /// unchanged.</summary>
    public static IReadOnlyList<KcapMcpServer> ForHarness(string vendor) =>
        [.. ForCursor.Select(s => s.Name == FlowsServerName
            ? s with { Args = [.. s.Args, DriverArg, vendor] }
            : s)];
}
