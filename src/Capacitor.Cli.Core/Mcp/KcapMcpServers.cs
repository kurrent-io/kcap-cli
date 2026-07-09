namespace Capacitor.Cli.Core.Mcp;

/// <summary>One kcap MCP server, described semantically (no harness field names).</summary>
public sealed record KcapMcpServer(string Name, string[] Args, bool NeedsProjectCwd, string? Description);

/// <summary>The single source of truth for the kcap MCP servers. Every writer
/// (Codex TOML, the JSON harnesses, the bundled `.mcp.json`) derives from this.</summary>
public static class KcapMcpServers {
    public const string Command = "kcap";

    public static readonly IReadOnlyList<KcapMcpServer> All = [
        new("kcap-review",   ["mcp", "review"],   NeedsProjectCwd: false,
            "PR review context tools — query implementation session transcripts."),
        new("kcap-sessions", ["mcp", "sessions"], NeedsProjectCwd: true,  null),
        new("kcap-flows",    ["mcp", "flows"],    NeedsProjectCwd: true,
            "Structured AI agent flows — launches a SEPARATE hosted participant agent; requires login + a running daemon."),
        new("kcap-memory",   ["mcp", "memory"],   NeedsProjectCwd: true,
            "Team memory — search, read, and save durable learnings."),
        new("kcap-workitems", ["mcp", "workitems"], NeedsProjectCwd: true,
            "Attach the current session to a work item (issue, PR, or a brand-new item), and list what a session is attached to."),
    ];

    /// <summary>Codex omits `kcap-flows` (AI-1056: paid hosted reviewer, Claude-only) and
    /// `kcap-workitems` (AI-1264: Claude Code plugin only, not yet exposed to Codex).</summary>
    public static IReadOnlyList<KcapMcpServer> ForCodex =>
        All.Where(s => s.Name is not ("kcap-flows" or "kcap-workitems")).ToArray();

    /// <summary>Cursor omits `kcap-workitems` (AI-1264: Claude Code plugin only, not yet
    /// exposed to Cursor). Unlike Codex, Cursor still gets `kcap-flows`.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCursor =>
        All.Where(s => s.Name != "kcap-workitems").ToArray();
}
