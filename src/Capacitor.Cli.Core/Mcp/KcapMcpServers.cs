namespace Capacitor.Cli.Core.Mcp;

/// <summary>One kcap MCP server, described semantically (no harness field names).
/// <paramref name="ReadOnly"/> marks a server whose tools are all side-effect-free (pure reads),
/// so it is safe to auto-approve on registration where the harness supports per-server trust —
/// see <see cref="McpConfigShape.Trust"/>. Servers that write (kcap-memory's save) or launch work
/// (kcap-flows' start_review_flow spawns a paid hosted reviewer) are NOT read-only and keep
/// prompting.</summary>
public sealed record KcapMcpServer(string Name, string[] Args, bool NeedsProjectCwd, string? Description, bool ReadOnly = false);

/// <summary>The single source of truth for the kcap MCP servers. Every writer
/// (Codex TOML, the JSON harnesses, the bundled `.mcp.json`) derives from this.</summary>
public static class KcapMcpServers {
    public const string Command = "kcap";

    public static readonly IReadOnlyList<KcapMcpServer> All = [
        new("kcap-review",   ["mcp", "review"],   NeedsProjectCwd: false,
            "PR review context tools — query implementation session transcripts.", ReadOnly: true),
        new("kcap-sessions", ["mcp", "sessions"], NeedsProjectCwd: true,  null, ReadOnly: true),
        new("kcap-flows",    ["mcp", "flows"],    NeedsProjectCwd: true,
            "Structured AI agent flows — launches a SEPARATE hosted participant agent; requires login + a running daemon."),
        new("kcap-memory",   ["mcp", "memory"],   NeedsProjectCwd: true,
            "Team memory — search, read, and save durable learnings."),
    ];

    /// <summary>Codex omits `kcap-flows` (AI-1056: paid hosted reviewer, Claude-only).</summary>
    public static IReadOnlyList<KcapMcpServer> ForCodex =>
        All.Where(s => s.Name != "kcap-flows").ToArray();
}
