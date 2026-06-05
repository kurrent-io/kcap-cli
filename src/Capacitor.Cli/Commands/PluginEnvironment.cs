using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Process-state seam for <see cref="PluginCommand"/>. Captures the values that
/// <c>kcap plugin install/remove</c> would otherwise read from
/// <see cref="Environment"/> / <see cref="Console"/>, so tests can supply
/// fakes without mutating shared process state (see AI-741).
/// </summary>
public sealed record PluginEnvironment(
    string      HomeDirectory,
    string?     PluginPath,
    TextWriter  Stdout,
    TextWriter  Stderr
) {
    public string ClaudeHome         => Path.Combine(HomeDirectory, ".claude");
    public string ClaudeUserSettings => Path.Combine(ClaudeHome, "settings.json");
    public string CodexHome          => Path.Combine(HomeDirectory, ".codex");
    public string CodexUserHooksJson => Path.Combine(CodexHome, "hooks.json");
    public string AgentsSkillsDir    => Path.Combine(HomeDirectory, ".agents", "skills");
    public string LegacyCodexSkills  => Path.Combine(CodexHome, "skills");

    public static PluginEnvironment FromProcess() => new(
        HomeDirectory: PathHelpers.HomeDirectory,
        PluginPath:    SetupCommand.ResolvePluginPath(),
        Stdout:        Console.Out,
        Stderr:        Console.Error
    );
}
