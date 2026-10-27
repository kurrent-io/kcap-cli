using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Services;

/// What `kcap plugin install` takes after the vendor flag for "record sessions" and "kcap tools"
/// answered separately. Tools are the MCP servers and what steers the agent to them.
public static class HarnessInstallOptions {
    /// Claude Code and Codex install their tools with capture; the two cannot be answered apart.
    public static bool ToolsBundled(HarnessId id) => id == HarnessId.Claude || id == HarnessId.Codex;

    public static IReadOnlyList<string> For(HarnessId id, bool record, bool tools) =>
        ToolsBundled(id)    ? []
        : record && !tools  ? [.. ToolParts(id).Select(part => $"--skip-{id.VendorId}-{part}")]
        : !record && tools  ? ["--tools-only"]
        :                     [];

    // Cursor and Kiro carry no instructions block of their own.
    static IEnumerable<string> ToolParts(HarnessId id) =>
        id == HarnessId.Cursor || id == HarnessId.Kiro ? ["mcp", "skills"] : ["mcp", "skills", "instructions"];
}
