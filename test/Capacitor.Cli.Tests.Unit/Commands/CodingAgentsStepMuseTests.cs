using Capacitor.Cli.Core.Mcp;
using static Capacitor.Cli.Commands.CodingAgentsStep;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class CodingAgentsStepMuseTests {
    const string SettingsPath = "/fake/.config/muse/settings.json";

    static readonly DetectedAgents MuseOnly = new(Claude: false, Codex: false, Cursor: false, Copilot: false, Muse: true);

    static Options Opts(bool skipMuse = false, bool skipMuseMcp = false) =>
        new(SkipClaude: true, SkipCodex: true, SkipCursor: true, SkipCopilot: true, NoPrompt: true,
            SkipMuse: skipMuse, SkipMuseMcp: skipMuseMcp);

    static Paths TestPaths() => new(
        ClaudeSettingsPath: "/fake/.claude/settings.json", ClaudeScopeLabel: "user", PluginDir: "/fake/plugin",
        CodexHooksPath: "/fake/.codex/hooks.json", CursorHooksPath: "/fake/.cursor/hooks.json",
        CopilotHooksPath: "/fake/.copilot/hooks/kcap.json", GeminiSettingsPath: "/fake/.gemini/settings.json",
        AgentsSkillsDir: "/fake/.agents/skills", LegacyCodexSkillsDir: "/fake/.codex/skills",
        MuseSettingsPath: SettingsPath);

    static Installers Wire(
            bool hooksOk, JsonMcpConfigWriter.Change mcp, List<string> hookCalls, List<string> mcpCalls, bool onPath = true) =>
        new(InstallClaudePlugin: (_, _) => true,
            InstallCodexHooks: _ => true,
            InstallCursorHooks: _ => true,
            InstallCopilotHooks: _ => true,
            InstallGeminiHooks: _ => true,
            CapacitorOnPath: () => onPath,
            InstallAgentSkills: (_, _) => true,
            CleanLegacyCodexSkills: _ => false,
            InstallMuseHooks: path => { hookCalls.Add(path); return hooksOk; },
            RegisterMuseMcp: () => { mcpCalls.Add(SettingsPath); return mcp; });

    [Test]
    public async Task Hooks_and_mcp_are_installed_into_settings_json() {
        List<string> hooks = [], mcps = [], lines = [];

        var result = await RunAsync(Opts(), MuseOnly, TestPaths(),
            Wire(true, JsonMcpConfigWriter.Change.Updated, hooks, mcps), _ => true, lines.Add);

        await Assert.That(result.MuseHooksInstalled).IsTrue();
        await Assert.That(result.MuseMcpRegistered).IsTrue();
        await Assert.That(result.AnyHooksInstalled).IsTrue();
        await Assert.That(hooks).IsEquivalentTo([SettingsPath]);
        await Assert.That(InstalledPaths(result, TestPaths())).Contains(SettingsPath);
    }

    [Test]
    public async Task A_settings_json_that_will_not_take_hooks_is_a_failure_and_skips_the_mcp_write() {
        List<string> hooks = [], mcps = [], lines = [];

        var result = await RunAsync(Opts(), MuseOnly, TestPaths(),
            Wire(false, JsonMcpConfigWriter.Change.Updated, hooks, mcps), _ => true, lines.Add);

        await Assert.That(result.MuseHooksInstalled).IsFalse();
        await Assert.That(result.MuseMcpRegistered).IsFalse();
        await Assert.That(mcps).IsEmpty();
        await Assert.That(lines).Contains(l => l.Contains("Could not install Muse hooks") && l.Contains("valid JSON"));
        await Assert.That(lines).DoesNotContain(l => l.Contains("Muse hooks installed"));
    }

    [Test]
    public async Task Hooks_are_not_installed_when_kcap_is_off_path() {
        List<string> hooks = [], mcps = [], lines = [];

        var result = await RunAsync(Opts(), MuseOnly, TestPaths(),
            Wire(true, JsonMcpConfigWriter.Change.Updated, hooks, mcps, onPath: false), _ => true, lines.Add);

        await Assert.That(result.MuseHooksInstalled).IsFalse();
        await Assert.That(hooks).IsEmpty();
        await Assert.That(lines).Contains(l => l.Contains("'kcap' is not on PATH"));
    }

    [Test]
    public async Task Mcp_failure_emits_a_warning() {
        List<string> hooks = [], mcps = [], lines = [];

        var result = await RunAsync(Opts(), MuseOnly, TestPaths(),
            Wire(true, JsonMcpConfigWriter.Change.Failed, hooks, mcps), _ => true, lines.Add);

        await Assert.That(result.MuseMcpRegistered).IsFalse();
        await Assert.That(lines).Contains(l => l.Contains("Could not register Muse MCP"));
    }

    [Test]
    public async Task Skip_flag_installs_nothing() {
        List<string> hooks = [], mcps = [], lines = [];

        var result = await RunAsync(Opts(skipMuse: true), MuseOnly, TestPaths(),
            Wire(true, JsonMcpConfigWriter.Change.Updated, hooks, mcps), _ => true, lines.Add);

        await Assert.That(result.MuseHooksInstalled).IsFalse();
        await Assert.That(hooks).IsEmpty();
        await Assert.That(mcps).IsEmpty();
    }
}
