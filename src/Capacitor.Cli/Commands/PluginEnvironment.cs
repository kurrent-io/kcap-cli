using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Process-state seam for <see cref="PluginCommand"/>. Captures the values that
/// <c>kcap plugin install/remove</c> would otherwise read from
/// <see cref="Environment"/> / <see cref="Console"/>, so tests can supply
/// fakes without mutating shared process state (see AI-741).
///
/// <see cref="ResolvePluginPath"/> is a delegate (not a string) so the
/// filesystem probing in <see cref="SetupCommand.ResolvePluginPath(string?)"/>
/// only runs on the install branches that actually need it — not on
/// <c>remove</c>, <c>--cursor</c>, or early-exit invocations.
/// </summary>
public sealed record PluginEnvironment(
    string         HomeDirectory,
    Func<string?>  ResolvePluginPath,
    TextWriter     Stdout,
    TextWriter     Stderr
) {
    public string ClaudeHome          => Path.Combine(HomeDirectory, ".claude");
    public string ClaudeUserSettings  => Path.Combine(ClaudeHome, "settings.json");
    public string CodexHome           => Path.Combine(HomeDirectory, ".codex");
    public string CodexUserHooksJson  => Path.Combine(CodexHome, "hooks.json");
    public string CursorUserHooksJson => CursorPaths.UserHooksJson(HomeDirectory);
    public string CopilotKcapHooksJson => CopilotPaths.KcapHooksJson(HomeDirectory);
    public string GeminiSettingsJson   => GeminiPaths.SettingsJson(HomeDirectory);
    public string PiKcapExtension      => PiPaths.KcapExtension(HomeDirectory);
    public string AgentsSkillsDir     => Path.Combine(HomeDirectory, ".agents", "skills");
    public string LegacyCodexSkills   => Path.Combine(CodexHome, "skills");

    public static PluginEnvironment FromProcess() => new(
        HomeDirectory:     PathHelpers.HomeDirectory,
        ResolvePluginPath: () => SetupCommand.ResolvePluginPath(),
        Stdout:            Console.Out,
        Stderr:            Console.Error
    );
}
