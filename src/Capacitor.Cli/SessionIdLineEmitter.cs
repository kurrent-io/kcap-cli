using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli;

/// <summary>The SessionStart line that states the session id on its own. The work-items and plans
/// nudges each state the id inside their text, so the line is emitted only when neither is: an agent
/// holding kcap tools would otherwise have no id to hand one that cannot resolve the session.</summary>
static class SessionIdLineEmitter {
    const int MaxSessionIdLength = 256;

    public static string? Resolve(HarnessId harness, string? sessionId, HarnessRegistry harnesses,
                                  string? workItemsNudge, string? plansNudge, string? codexConfigPath = null) {
        if (!string.IsNullOrWhiteSpace(workItemsNudge) || !string.IsNullOrWhiteSpace(plansNudge)) return null;
        if (!AnyServerRegisteredFor(harness, harnesses, codexConfigPath)) return null;

        return Build(sessionId);
    }

    public static bool AnyServerRegisteredFor(HarnessId harness, HarnessRegistry harnesses, string? codexConfigPath = null) =>
        KcapMcpServers.All.Any(s => McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, s.Name, codexConfigPath));

    public static string? Build(string? sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var id = sessionId.Trim();
        if (id.Length > MaxSessionIdLength) return null;

        // Rendered verbatim inside a code span: a backtick or control char would break the span or
        // smuggle formatting, so such an id suppresses the line instead.
        foreach (var c in id)
            if (c == '`' || char.IsControl(c)) return null;

        return $"Kurrent Capacitor session id: `{id}`. Pass it as `session_id` to a kcap MCP tool that cannot resolve the session by itself.";
    }
}
