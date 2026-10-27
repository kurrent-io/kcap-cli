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

    // The skip flags each vendor's install takes; one it does not take is ignored, not refused, so a
    // wrong name would leave that part installed without a word.
    static IEnumerable<string> ToolParts(HarnessId id) =>
        id == HarnessId.Grok ? ["skills"]
        : id == HarnessId.Cursor || id == HarnessId.Kiro || id == HarnessId.MistralVibe || id == HarnessId.Muse ? ["mcp", "skills"]
        : ["mcp", "skills", "instructions"];
}
