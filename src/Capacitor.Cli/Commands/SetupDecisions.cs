namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure decision helpers for <c>kcap setup</c>'s Step 4 (coding agents). Kept separate from
/// <see cref="CodingAgentsStep"/> so <c>SetupCommand</c>'s consent logic — building the
/// detected-agent summary and deciding whether to install at all — is unit-testable without
/// touching any installer delegate, filesystem, or console.
/// </summary>
internal static class SetupDecisions {
    /// <summary>
    /// Human-readable, comma-joined list of detected harnesses (in a stable, user-facing
    /// order), or null when none are detected. Kiro is annotated because installing for it
    /// makes a material change (clones the user's default agent and sets kcap as default).
    /// </summary>
    public static string? DetectedAgentsSummary(CodingAgentsStep.DetectedAgents d) {
        var names = new List<string>();

        if (d.Claude)      names.Add("Claude Code");
        if (d.Codex)       names.Add("Codex");
        if (d.Cursor)      names.Add("Cursor");
        if (d.Copilot)     names.Add("Copilot");
        if (d.Gemini)      names.Add("Gemini");
        if (d.Kiro)        names.Add("Kiro (installing sets kcap as your default Kiro agent)");
        if (d.Pi)          names.Add("Pi");
        if (d.OpenCode)    names.Add("OpenCode");
        if (d.Antigravity) names.Add("Antigravity");

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// The single unified install-consent decision that replaces the nine per-vendor prompts.
    /// No agent detected → false, no prompt (CodingAgentsStep.RunAsync's own no-agents
    /// early-return owns the warning). Otherwise <paramref name="noPrompt"/> short-circuits to
    /// true — preserving today's unattended `kcap setup --no-prompt` behaviour — else the
    /// caller's yes/no prompt decides.
    /// </summary>
    public static bool DecideInstallAgents(CodingAgentsStep.DetectedAgents d, bool noPrompt, Func<string, bool> promptYesNo) {
        if (DetectedAgentsSummary(d) is null) return false;

        return noPrompt || promptYesNo("Install kcap for these agents (hooks, skills, instructions, MCP)?");
    }
}
