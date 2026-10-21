using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Core.Harness.Codex;

/// A Codex hook names its calls with Claude's tool vocabulary (<c>Bash</c> with <c>tool_input.command</c>), so the
/// Claude normalizer reads them; the action is Codex's, which rules scoped to a vendor see.
public static class CodexHookActions {
    public static CanonicalAction Normalize(string? toolName, JsonElement? toolInput, string? cwd) =>
        ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd) with { Vendor = "codex" };
}
