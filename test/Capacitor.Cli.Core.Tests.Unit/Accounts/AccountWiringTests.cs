using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountWiringTests {
    [TempHome] public required TempHome Home { get; init; }

    VendorAccount Account(HarnessId vendor, string dirName) {
        var dir = Directory.CreateDirectory(Home.PathTo(dirName)).FullName;
        return new(dirName, vendor, AccountDirectory.Normalize(dir), dirName, DateTimeOffset.UnixEpoch);
    }

    WiringOptions Options() {
        var plugin = Directory.CreateDirectory(Home.PathTo("plugin")).FullName;
        foreach (var n in AgentsSkillsInstaller.SourceNames)
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(plugin, "skills", n)).FullName, "SKILL.md"), "---\nname: " + n + "\n---\n");
        return new(plugin, Home.PathTo(".agents", "skills"), () => "/usr/local/bin/kcap", NetworkAllowDomains: null);
    }

    static void WriteInstallRecord(string accountDir, string installPath) {
        var plugins = Directory.CreateDirectory(Path.Combine(accountDir, "plugins")).FullName;
        File.WriteAllText(Path.Combine(plugins, "installed_plugins.json"), $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "user", "installPath": {{JsonValue.Create(installPath).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);
    }

    [Test]
    public async Task Wiring_a_claude_account_enables_the_plugin_in_its_own_settings() {
        var work = Account(HarnessId.Claude, ".claude-work");

        var steps = AccountWiring.Wire(work, Home, Options());

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Wiring_a_fresh_claude_account_reports_installed_until_claude_installs_the_plugin() {
        var work = Account(HarnessId.Claude, ".claude-work");

        AccountWiring.Wire(work, Home, Options());

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    public async Task Wiring_two_codex_accounts_tracks_mcp_ownership_per_home() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");

        AccountWiring.Wire(a, Home, Options());
        AccountWiring.Wire(b, Home, Options());

        await Assert.That(File.Exists(Home.PathTo(".codex", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".codex-b", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Unwire_removes_only_what_kcap_added() {
        var b = Account(HarnessId.Codex, ".codex-b");
        File.WriteAllText(Home.PathTo(".codex-b", "config.toml"), "[mcp_servers.mine]\ncommand = \"x\"\n");
        AccountWiring.Wire(b, Home, Options());

        AccountWiring.Unwire(b, Home);

        await Assert.That(File.ReadAllText(Home.PathTo(".codex-b", "config.toml"))).Contains("[mcp_servers.mine]");
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsFalse();
    }

    [Test]
    public async Task Unwiring_one_of_two_codex_accounts_keeps_the_shared_agent_skills() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");
        AccountWiring.Wire(a, Home, Options());
        AccountWiring.Wire(b, Home, Options());

        AccountWiring.Unwire(b, Home);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(Home.PathTo(".agents", "skills"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Malformed_settings_make_one_account_broken_and_leave_the_file() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), "{ nope");

        var steps = AccountWiring.Wire(work, Home, Options());

        await Assert.That(AccountWiring.Succeeded(steps)).IsFalse();
        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Broken);
        await Assert.That(File.ReadAllText(Home.PathTo(".claude-work", "settings.json"))).IsEqualTo("{ nope");
    }

    [Test]
    public async Task Claude_plugin_enabled_without_its_payload_is_broken() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), """{ "enabledPlugins": { "kcap@kcap": true } }""");
        WriteInstallRecord(work.Directory, Home.PathTo(".claude-work", "plugins", "cache", "missing"));

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Broken);
    }

    [Test]
    public async Task Claude_plugin_enabled_before_first_launch_is_installed() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    public async Task Claude_plugin_with_its_payload_is_recording() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), """{ "enabledPlugins": { "kcap@kcap": true } }""");
        var install = Directory.CreateDirectory(Home.PathTo(".claude-work", "plugins", "cache", "kcap")).FullName;
        File.WriteAllText(Path.Combine(install, ".mcp.json"), "{}");
        WriteInstallRecord(work.Directory, install);

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Recording);
    }

    [Test]
    public async Task Codex_hooks_present_report_installed() {
        var b = Account(HarnessId.Codex, ".codex-b");
        AccountWiring.Wire(b, Home, Options());

        await Assert.That(AccountWiring.State(b, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    [Arguments("[1,2]")]
    [Arguments("null")]
    [Arguments("\"text\"")]
    [Arguments("{ \"enabledPlugins\": { \"kcap@kcap\": \"yes\" } }")]
    public async Task State_never_throws_on_odd_settings_content(string content) {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), content);

        var state = AccountWiring.State(work, Home);

        await Assert.That(state is RecordingState.Broken or RecordingState.NotWired).IsTrue();
    }

    [Test]
    public async Task An_untouched_account_is_not_wired() {
        await Assert.That(AccountWiring.State(Account(HarnessId.Claude, ".claude-x"), Home)).IsEqualTo(RecordingState.NotWired);
    }

    [Test]
    public async Task Codex_config_stays_owner_only_after_wiring() {
        if (OperatingSystem.IsWindows()) return;
        var b = Account(HarnessId.Codex, ".codex-b");

        AccountWiring.Wire(b, Home, Options());

        await Assert.That(File.GetUnixFileMode(Home.PathTo(".codex-b", "config.toml")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
