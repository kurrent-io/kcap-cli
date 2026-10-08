using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AccountSetupStepTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    AccountStore Store => new(Tmp.PathTo("accounts"));

    AccountSetupStep Sut() => new(Store, TestPluginEnvironment.For(Home, PluginDir()), TimeProvider.System);

    string PluginDir() {
        var dir = Directory.CreateDirectory(Home.PathTo("plugin")).FullName;
        foreach (var n in AgentsSkillsInstaller.SourceNames) {
            var skill = Directory.CreateDirectory(Path.Combine(dir, "skills", n)).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {n}\n---\n# {n}");
        }
        return dir;
    }

    static CodingAgentsStep.Options Options(bool noPrompt, bool skipClaude = false, bool skipCodex = false, bool installAgents = true) =>
        new(SkipClaude: skipClaude, SkipCodex: skipCodex, SkipCursor: true, SkipCopilot: true, NoPrompt: noPrompt, InstallAgents: installAgents);

    [Test]
    public async Task No_prompt_lists_candidates_without_adding_them() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var lines = new List<string>();

        Sut().Run(Options(noPrompt: true), true, _ => throw new InvalidOperationException("must not prompt"), lines.Add);

        await Assert.That(lines.Any(l => l.Contains("kcap accounts add claude"))).IsTrue();
        await Assert.That(Store.Find(HarnessId.Claude, Home.PathTo(".claude-work"))).IsNull();
    }

    [Test]
    public async Task A_yes_adds_and_wires_the_candidate() {
        Home.CreateFile(".claude-work/settings.json", "{}");

        Sut().Run(Options(noPrompt: false), true, _ => true, _ => { });

        await Assert.That(Store.Find(HarnessId.Claude, Home.PathTo(".claude-work"))).IsNotNull();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task A_skipped_vendor_is_neither_adopted_nor_offered() {
        Home.CreateFile(".codex-b/config.toml", "");
        CodexHooksWriter.Install(Home.PathTo(".codex", "hooks.json"));
        var asked = new List<string>();

        Sut().Run(Options(noPrompt: false, skipCodex: true), true, q => { asked.Add(q); return true; }, _ => { });

        await Assert.That(asked.Any(q => q.Contains(".codex-b"))).IsFalse();
        await Assert.That(AccountAdoption.Of(Store, HarnessId.Codex).Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_unreadable_registry_warns_and_does_not_throw() {
        Directory.CreateDirectory(Tmp.PathTo("accounts"));
        File.WriteAllText(Path.Combine(Tmp.PathTo("accounts"), "accounts.json"), "{not json");
        var lines = new List<string>();

        Sut().Run(Options(noPrompt: true), true, _ => true, lines.Add);

        await Assert.That(lines.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_seeded_default_codex_is_adopted_when_not_skipped() {
        CodexHooksWriter.Install(Home.PathTo(".codex", "hooks.json"));

        Sut().Run(Options(noPrompt: true), true, _ => true, _ => { });

        await Assert.That(AccountAdoption.Of(Store, HarnessId.Codex).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Declined_agent_setup_does_nothing() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var lines = new List<string>();

        Sut().Run(Options(noPrompt: false, installAgents: false), true, _ => throw new InvalidOperationException("must not prompt"), lines.Add);

        await Assert.That(lines.Count).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(Tmp.PathTo("accounts"), "accounts.json"))).IsFalse();
    }

    [Test]
    public async Task A_non_interactive_console_prints_the_hint_instead_of_prompting() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var lines = new List<string>();

        Sut().Run(Options(noPrompt: false), false, _ => throw new InvalidOperationException("must not prompt"), lines.Add);

        await Assert.That(lines.Any(l => l.Contains("kcap accounts add claude"))).IsTrue();
    }

    [Test]
    public async Task A_prompt_that_throws_degrades_to_the_hint() {
        Home.CreateFile(".claude-work/settings.json", "{}");
        var lines = new List<string>();

        Sut().Run(Options(noPrompt: false), true, _ => throw new InvalidOperationException("no console"), lines.Add);

        await Assert.That(lines.Any(l => l.Contains("kcap accounts add claude"))).IsTrue();
        await Assert.That(Store.Find(HarnessId.Claude, Home.PathTo(".claude-work"))).IsNull();
    }
}
