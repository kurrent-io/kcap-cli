namespace kapacitor.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kapacitor setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    internal record Options(bool SkipClaude, bool SkipCodex, bool NoPrompt, bool LegacyProjectScope);
    internal record DetectedAgents(bool Claude, bool Codex);
    internal record Paths(
        string ClaudeSettingsPath,
        string? PluginDir,
        string CodexHooksPath,
        string CodexSkillsDir);
    internal record Installers(
        Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
        Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
        Func<string /*src*/, string /*dst*/, bool>                InstallCodexSkills);
    internal record Result(bool ClaudeInstalled, bool CodexHooksInstalled, bool CodexSkillsInstalled);

    /// <summary>
    /// Drives the agent-detection branches and dispatches to the installer
    /// delegates. Subsequent tasks fill in Claude, Codex hooks, Codex skills,
    /// and neither-detected behaviour.
    /// </summary>
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        // Filled in by Tasks 4–8.
        return Task.FromResult(new Result(false, false, false));
    }
}
