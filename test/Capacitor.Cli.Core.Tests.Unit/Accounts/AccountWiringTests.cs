using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountWiringTests {
    [TempHome] public required TempHome Home { get; init; }

    AccountStore Store => new(Home.PathTo("accounts"));

    VendorAccount Account(HarnessId vendor, string dirName) {
        string dir  = Home.CreateDir(dirName);
        var account = new VendorAccount(dirName, vendor, AccountDirectory.Normalize(dir), dirName, DateTimeOffset.UnixEpoch);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts, account] }, 0));
        return account;
    }

    IReadOnlyList<WiringStep> Wire(VendorAccount account, AccountStore? store = null) {
        store ??= Store;
        using var _ = store.Lock();
        return AccountWiring.Wire(account, Home, Options(), store);
    }

    IReadOnlyList<WiringStep> Unwire(VendorAccount account) {
        var store = Store;
        using var _ = store.Lock();
        return AccountWiring.Unwire(account, Home, store);
    }

    string ClaudeSettings(string dirName) => Home.PathTo(dirName, "settings.json");

    string? StatusLineCommand(string dirName) =>
        JsonNode.Parse(File.ReadAllText(ClaudeSettings(dirName)))!["statusLine"]?["command"]?.GetValue<string>();

    StatusLineBackup? BackupOf(VendorAccount account) => Store.Load().Accounts.Single(a => a.Id == account.Id).StatusLine;

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

        var steps = Wire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Wiring_a_fresh_claude_account_reports_installed_until_claude_installs_the_plugin() {
        var work = Account(HarnessId.Claude, ".claude-work");

        Wire(work);

        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    public async Task Wiring_two_codex_accounts_tracks_mcp_ownership_per_home() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");

        Wire(a);
        Wire(b);

        await Assert.That(File.Exists(Home.PathTo(".codex", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(File.Exists(Home.PathTo(".codex-b", "mcp-ownership-v1.json"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Unwire_removes_only_what_kcap_added() {
        var b = Account(HarnessId.Codex, ".codex-b");
        File.WriteAllText(Home.PathTo(".codex-b", "config.toml"), "[mcp_servers.mine]\ncommand = \"x\"\n");
        Wire(b);

        Unwire(b);

        await Assert.That(File.ReadAllText(Home.PathTo(".codex-b", "config.toml"))).Contains("[mcp_servers.mine]");
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsFalse();
    }

    [Test]
    public async Task Unwiring_one_of_two_codex_accounts_keeps_the_shared_agent_skills() {
        var a = Account(HarnessId.Codex, ".codex");
        var b = Account(HarnessId.Codex, ".codex-b");
        Wire(a);
        Wire(b);

        Unwire(b);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(Home.PathTo(".agents", "skills"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Malformed_settings_make_one_account_broken_and_leave_the_file() {
        var work = Account(HarnessId.Claude, ".claude-work");
        File.WriteAllText(Home.PathTo(".claude-work", "settings.json"), "{ nope");

        var steps = Wire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsFalse();
        await Assert.That(AccountWiring.State(work, Home)).IsEqualTo(RecordingState.Broken);
        await Assert.That(File.ReadAllText(Home.PathTo(".claude-work", "settings.json"))).IsEqualTo("{ nope");
    }

    [Test]
    [Arguments(HarnessId.Claude, ".claude-work", "settings.json")]
    [Arguments(HarnessId.Codex, ".codex-work", "hooks.json")]
    public async Task An_empty_settings_file_is_not_broken(HarnessId vendor, string dir, string file) {
        var account = Account(vendor, dir);
        File.WriteAllText(Home.PathTo(dir, file), " \n");

        await Assert.That(AccountWiring.State(account, Home)).IsEqualTo(RecordingState.NotWired);
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
        Wire(b);

        await Assert.That(AccountWiring.State(b, Home)).IsEqualTo(RecordingState.Installed);
    }

    /// Codex runs none of a hooks file that starts with a byte-order mark, so the account is broken,
    /// and rewiring it rewrites the file without the mark.
    [Test]
    public async Task Codex_hooks_with_a_byte_order_mark_report_broken_until_rewired() {
        var b     = Account(HarnessId.Codex, ".codex-b");
        Wire(b);
        var hooks = Home.PathTo(".codex-b", "hooks.json");
        Home.CreateFile(".codex-b/hooks.json", "\uFEFF" + File.ReadAllText(hooks));

        await Assert.That(AccountWiring.State(b, Home)).IsEqualTo(RecordingState.Broken);

        Wire(b);

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

        Wire(b);

        await Assert.That(File.GetUnixFileMode(Home.PathTo(".codex-b", "config.toml")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Wiring_a_claude_account_wraps_its_status_line_and_unwiring_restores_it() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");

        var steps = Wire(work);

        await Assert.That(steps.Single(s => s.Name == "statusline").Succeeded).IsTrue();
        await Assert.That(ClaudeStatusLineCommand.IsOurs(StatusLineCommand(".claude-work"))).IsTrue();
        await Assert.That(BackupOf(work)).IsEqualTo(new StatusLineBackup("echo mine"));

        var unwired = Unwire(work);

        await Assert.That(AccountWiring.Succeeded(unwired)).IsTrue();
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo("echo mine");
        await Assert.That(BackupOf(work)).IsNull();
    }

    [Test]
    public async Task Stale_account_snapshot_unwire_uses_the_current_backup() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo old"}}""");
        Wire(work);
        var snapshot = Store.Load().Accounts.Single(a => a.Id == work.Id);

        var other = new AccountStore(Home.PathTo("accounts"));
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo newer"}}""");
        Wire(work, other);

        Unwire(snapshot);

        await Assert.That(snapshot.StatusLine).IsEqualTo(new StatusLineBackup("echo old"));
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo("echo newer");
    }

    [Test]
    public async Task User_edit_then_unwire_keeps_the_edit_and_discards_backup() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Wire(work);
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo replaced"}}""");

        var steps = Unwire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo("echo replaced");
        await Assert.That(BackupOf(work)).IsNull();
    }

    [Test]
    public async Task Missing_backup_with_ours_installed_removes_command_and_reports_a_warning() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Wire(work);
        Store.Mutate(r => (r with { Accounts = [.. r.Accounts.Select(a => a with { StatusLine = null })] }, 0));

        var steps = Unwire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsFalse();
        await Assert.That(AccountWiring.HasFatalFailure(steps)).IsFalse();
        await Assert.That(AccountWiring.FailureSummary(steps)).Contains("statusline");
        await Assert.That(JsonNode.Parse(File.ReadAllText(ClaudeSettings(".claude-work")))!.AsObject().ContainsKey("statusLine")).IsFalse();
    }

    [Test]
    public async Task A_re_adopted_account_recovers_the_original_from_a_lost_entry_guard() {
        const string original = "echo 'it'\"'\"'s' # mine\ncat <<'EOF'\nline\nEOF";
        var work     = Account(HarnessId.Claude, ".claude-work");
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = ClaudeStatusLineCommand.For("lost-entry", original, true) } };
        Home.CreateFile(".claude-work/settings.json", settings.ToJsonString());

        var steps = Wire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(BackupOf(work)).IsEqualTo(new StatusLineBackup(original));
        await Assert.That(ClaudeStatusLineCommand.AccountOf(StatusLineCommand(".claude-work"))).IsEqualTo(work.Id);

        var unwired = Unwire(work);

        await Assert.That(AccountWiring.Succeeded(unwired)).IsTrue();
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo(original);
    }

    [Test]
    public async Task Malformed_settings_left_untouched_and_account_reports_broken() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":"echo mine"}""");

        var steps = Wire(work);

        var statusLine = steps.Single(s => s.Name == "statusline");
        await Assert.That(statusLine.Succeeded).IsFalse();
        await Assert.That(statusLine.Detail).Contains("unreadable");
        await Assert.That(AccountWiring.HasFatalFailure(steps)).IsTrue();
        await Assert.That(JsonNode.Parse(File.ReadAllText(ClaudeSettings(".claude-work")))!["statusLine"]!.GetValue<string>()).IsEqualTo("echo mine");
        await Assert.That(BackupOf(work)).IsNull();
    }

    [Test]
    public async Task An_account_removed_before_the_lock_fails_the_status_line_step_and_edits_nothing() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        Store.Mutate(r => (r with { Accounts = [] }, 0));

        var steps = Wire(work);

        var statusLine = steps.Single(s => s.Name == "statusline");
        await Assert.That(statusLine.Succeeded).IsFalse();
        await Assert.That(statusLine.Detail).IsEqualTo("account removed");
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo("echo mine");
    }

    [Test]
    public async Task The_status_line_step_alone_wraps_and_restores() {
        var work = Account(HarnessId.Claude, ".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{"statusLine":{"type":"command","command":"echo mine"}}""");
        var store = Store;

        WiringStep wired, unwired;
        using (store.Lock()) wired = AccountWiring.WireStatusLine(work.Id, Home, store);
        var installed = StatusLineCommand(".claude-work");
        using (store.Lock()) unwired = AccountWiring.UnwireStatusLine(work.Id, Home, store);

        await Assert.That(wired.Succeeded).IsTrue();
        await Assert.That(ClaudeStatusLineCommand.IsOurs(installed)).IsTrue();
        await Assert.That(unwired.Succeeded).IsTrue();
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo("echo mine");
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(ClaudeSettings(".claude-work"))).IsFalse();
    }

    sealed class SimulatedCrash : Exception;

    public static IEnumerable<Func<(string Name, Func<AccountStore, string, string> Point)>> InterruptionPoints() {
        yield return () => ("before the backup save", (store, _) => $"rename:{store.RegistryPath}");
        yield return () => ("after the backup, before the settings write", (store, _) => $"renamed:{store.RegistryPath}");
        yield return () => ("during the settings write", (_, settings) => $"rename:{settings}");
        yield return () => ("after the settings write", (_, settings) => $"renamed:{settings}");
    }

    [Test]
    [MethodDataSource(nameof(InterruptionPoints))]
    public async Task Interrupted_install((string Name, Func<AccountStore, string, string> Point) c) {
        var work     = Account(HarnessId.Claude, ".claude-work");
        var settings = Home.CreateFile([".claude-work", "settings.json"], """{"model":"opus","statusLine":{"type":"command","command":"echo mine","padding":3}}""");
        var store    = Store;
        // A fault point matches by exact string, so arm it with the path wiring derives, not the test's spelling of it.
        var point    = c.Point(store, AccountLayouts.Claude(Home, work.Directory).UserSettings);

        using (FaultPoints.Arm(p => { if (p == point) throw new SimulatedCrash(); })) {
            await Assert.That(() => {
                using var _ = store.Lock();
                AccountWiring.WireStatusLine(work.Id, Home, store);
            }).Throws<SimulatedCrash>();
        }

        var interrupted = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(interrupted["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(interrupted["statusLine"]!["padding"]!.GetValue<int>()).IsEqualTo(3);

        var steps = Wire(work);

        await Assert.That(AccountWiring.Succeeded(steps)).IsTrue();
        await Assert.That(StatusLineCommand(".claude-work")).IsEqualTo(ClaudeStatusLineCommand.For(work.Id, "echo mine", ClaudeStatusLineCommand.PosixShell(Environment.GetEnvironmentVariable)));
        await Assert.That(BackupOf(work)).IsEqualTo(new StatusLineBackup("echo mine"));
        await Assert.That(JsonNode.Parse(File.ReadAllText(settings))!["model"]!.GetValue<string>()).IsEqualTo("opus");
    }

    [Test]
    public async Task A_failed_network_step_alone_is_not_fatal() {
        WiringStep[] steps = [new("hooks", true, "Changed"), new("network", false, "Failed")];

        await Assert.That(AccountWiring.HasFatalFailure(steps)).IsFalse();
        await Assert.That(AccountWiring.Succeeded(steps)).IsFalse();
    }

    [Test]
    public async Task Any_other_failed_step_is_fatal() {
        WiringStep[] steps = [new("network", false, "Failed"), new("mcp", false, "Failed")];

        await Assert.That(AccountWiring.HasFatalFailure(steps)).IsTrue();
        await Assert.That(AccountWiring.HasFatalFailure([new WiringStep("hooks", true, "Changed")])).IsFalse();
    }

    [Test]
    public async Task Failure_summary_lists_only_failed_steps() {
        WiringStep[] steps = [new("hooks", true, "Changed"), new("network", false, "Failed"), new("mcp", false, "Malformed")];

        await Assert.That(AccountWiring.FailureSummary(steps)).IsEqualTo("network: Failed, mcp: Malformed");
        await Assert.That(AccountWiring.FailureSummary([new WiringStep("hooks", true, "x")])).IsEqualTo("");
    }

    VendorAccount WiredCodex(string? trust, bool fingerprintCurrent = true) {
        var account = Account(HarnessId.Codex, ".codex");
        Wire(account);
        var fingerprint = CodexTrustFingerprint.Of(AccountLayouts.Codex(Home, account.Directory));
        return account with { HookTrust = trust, HookTrustFingerprint = trust is null ? null : fingerprintCurrent ? fingerprint : "stale" };
    }

    [Test]
    public async Task Codex_trusted_for_the_current_files_is_recording() =>
        await Assert.That(AccountWiring.State(WiredCodex(HookTrustStates.Trusted), Home)).IsEqualTo(RecordingState.Recording);

    [Test]
    public async Task Codex_trusted_for_other_files_stays_installed() {
        var account = WiredCodex(HookTrustStates.Trusted);
        File.AppendAllText(Home.PathTo(".codex", "config.toml"), "\n# edited\n");

        await Assert.That(AccountWiring.State(account, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test]
    public async Task Codex_untrusted_stays_installed() =>
        await Assert.That(AccountWiring.State(WiredCodex(HookTrustStates.Untrusted), Home)).IsEqualTo(RecordingState.Installed);

    [Test]
    public async Task Codex_without_trust_fields_stays_installed() =>
        await Assert.That(AccountWiring.State(WiredCodex(null), Home)).IsEqualTo(RecordingState.Installed);

    [Test]
    public async Task Codex_not_wired_ignores_a_stored_trust() {
        var account = Account(HarnessId.Codex, ".codex") with { HookTrust = HookTrustStates.Trusted };
        Home.CreateFile(".codex/hooks.json", "{}");
        account = account with { HookTrustFingerprint = CodexTrustFingerprint.Of(AccountLayouts.Codex(Home, account.Directory)) };

        await Assert.That(AccountWiring.State(account, Home)).IsEqualTo(RecordingState.NotWired);
    }

    [Test]
    public async Task Codex_trusted_without_a_fingerprint_stays_installed() {
        var account = WiredCodex(null) with { HookTrust = HookTrustStates.Trusted };

        await Assert.That(AccountWiring.State(account, Home)).IsEqualTo(RecordingState.Installed);
    }

    [Test, ExcludeOn(TUnit.Core.Enums.OS.Windows), System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Codex_trusted_with_an_unreadable_file_stays_installed() {
        if (Environment.UserName == "root") {
            Skip.Test("root reads a mode-000 file");
        }

        var account = WiredCodex(HookTrustStates.Trusted);
        var config  = Home.CreateFile(".codex/config.toml", "a = 1");
        account = account with { HookTrustFingerprint = CodexTrustFingerprint.Of(AccountLayouts.Codex(Home, account.Directory)) };
        File.SetUnixFileMode(config, UnixFileMode.None);
        try {
            await Assert.That(AccountWiring.State(account, Home)).IsEqualTo(RecordingState.Installed);
        } finally {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
