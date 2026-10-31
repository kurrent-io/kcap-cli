using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class AccountsCommandTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    AccountStore Store => new(Tmp.PathTo("accounts"));

    AccountsCommand Sut(IAccountProbe? probe = null) =>
        new(Store, TestPluginEnvironment.For(Home, PluginDir()), TimeProvider.System) { ProbeFor = _ => probe ?? StubAccountProbe.SignedIn };

    string PluginDir() {
        var dir = Directory.CreateDirectory(Home.PathTo("plugin")).FullName;
        foreach (var n in AgentsSkillsInstaller.SourceNames) {
            var skill = Directory.CreateDirectory(Path.Combine(dir, "skills", n)).FullName;
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {n}\n---\n# {n}");
        }
        return dir;
    }

    string ClaudeDir(string name = ".claude-work") => Directory.CreateDirectory(Home.PathTo(name)).FullName;

    [Test]
    public async Task Add_registers_and_wires_a_claude_account() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Find(HarnessId.Claude, dir)).IsNotNull();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Path.Combine(dir, "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Add_twice_keeps_one_entry() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();

        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        await Sut().HandleAsync(["accounts", "add", "claude", dir + "/"]);

        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Add_refuses_a_missing_directory() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", Home.PathTo("nope")]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Add_accepts_a_codex_directory_and_wires_hooks() {
        var dir = ClaudeDir(".codex-b");
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "codex", dir]);

        await Assert.That(exit).IsEqualTo(0).Because(capture.GetCapturedError());
        await Assert.That(File.Exists(Path.Combine(dir, "hooks.json"))).IsTrue();
        await Assert.That(capture.GetCapturedOutput()).Contains("Trust");
    }

    [Test]
    public async Task Remove_unwires_and_forgets() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        var exit = await Sut().HandleAsync(["accounts", "remove", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Path.Combine(dir, "settings.json"))).IsFalse();
        await Assert.That(Directory.Exists(dir)).IsTrue();
    }

    [Test]
    public async Task Remove_deletes_the_reading() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        var id = Store.Load().Accounts.Single().Id;
        await Assert.That(Store.Readings.TryRecord(id, (f, _) => f)).IsTrue();

        var exit = await Sut().HandleAsync(["accounts", "remove", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Readings.LoadAll().ContainsKey(id)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Store.Directory, "readings", $"{id}.json"))).IsFalse();
    }

    [Test]
    public async Task Rename_marks_the_label_as_user_set() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Sut().HandleAsync(["accounts", "rename", dir, ".claude-work"]);

        await Assert.That(Store.Load().Accounts.Single().LabelSetByUser == true).IsTrue();
    }

    [Test]
    public async Task Remove_by_id_prefix_works() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        var id = Store.Load().Accounts.Single().Id;

        var exit = await Sut().HandleAsync(["accounts", "remove", id[..8]]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_with_a_short_prefix_does_not_match_by_id() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        var id = Store.Load().Accounts.Single().Id;

        var exit = await Sut().HandleAsync(["accounts", "remove", id[..3]]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_of_an_unknown_account_exits_1() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Sut().HandleAsync(["accounts", "remove", "zzzzzzzz"]);

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task List_shows_state_and_undiscovered_candidates() {
        var dir = ClaudeDir();
        Home.CreateFile(".codex-b/config.toml", "");
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Sut().HandleAsync(["accounts", "list"]);

        var output = capture.GetCapturedOutput();
        await Assert.That(output).Contains(".claude-work");
        await Assert.That(output).Contains("kcap accounts add codex");
    }

    [Test]
    public async Task List_quotes_a_candidate_directory_containing_a_space() {
        Home.CreateFile(".codex b/config.toml", "");
        var dir = AccountDirectory.Normalize(Home.PathTo(".codex b"));
        using var capture = ConsoleOutput.StartCapture();

        await Sut().HandleAsync(["accounts", "list"]);

        await Assert.That(capture.GetCapturedOutput())
            .Contains($"kcap accounts add codex {(OperatingSystem.IsWindows() ? $"\"{dir}\"" : $"'{dir}'")}");
    }

    [Test]
    public async Task List_reports_a_corrupt_registry_and_exits_1() {
        Tmp.CreateFile("accounts/accounts.json", "{ not json");
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Sut().HandleAsync(["accounts", "list"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("accounts.json");
    }

    [Test]
    public async Task Rename_changes_the_label() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);

        await Sut().HandleAsync(["accounts", "rename", dir, "Work"]);

        await Assert.That(Store.Load().Accounts.Single().Label).IsEqualTo("Work");
    }

    [Test]
    public async Task Rewire_reinstalls_into_every_account() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", dir]);
        File.Delete(Path.Combine(dir, "settings.json"));

        var exit = await Sut().HandleAsync(["accounts", "rewire"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Path.Combine(dir, "settings.json"))).IsTrue();
    }

    async Task Quietly(params string[] args) {
        using var capture = ConsoleOutput.StartFullCapture();
        await Sut().HandleAsync(args);
    }

    static AccountActionResultDto SingleResult(string stdout) {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) throw new InvalidOperationException($"Expected one stdout line, got {lines.Length}: {stdout}");
        return JsonSerializer.Deserialize(lines[0], AccountActionResultJsonContext.Default.AccountActionResultDto)!;
    }

    [Test]
    public async Task Rewire_that_changes_nothing_still_increments_the_revision() {
        var dir      = ClaudeDir();
        var settings = Path.Combine(dir, "settings.json");
        await Quietly("accounts", "add", "claude", dir);
        var revision = Store.Load().Revision;
        var before   = File.ReadAllText(settings);
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "rewire", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo(before);
        await Assert.That(Store.Load().Revision).IsEqualTo(revision + 1);
    }

    [Test]
    public async Task Rewire_of_an_account_removed_meanwhile_reports_the_conflict_not_success() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        var sut = Sut();
        sut.BetweenWiringAndStamp = () => Store.Mutate(r => (r with { Accounts = [] }, 0));
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await sut.HandleAsync(["accounts", "rewire", dir, "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Error).Contains("removed while it was being wired");
        await Assert.That(capture.GetCapturedError()).Contains("removed while it was being wired");
        await Assert.That(Store.Load().Accounts).IsEmpty();
    }

    [Test]
    public async Task Add_whose_wiring_fails_registers_and_reports_failure_with_the_committed_revision() {
        var dir = ClaudeDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "not json");
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", dir, "--json"]);

        var result   = SingleResult(capture.GetCapturedOutput());
        var registry = Store.Load();
        var account  = registry.Accounts.Single();
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.AccountId).IsEqualTo(account.Id);
        await Assert.That(result.Revision).IsEqualTo(registry.Revision);
        await Assert.That(result.Steps.Where(s => !s.Succeeded).Select(s => s.Name)).IsEquivalentTo(["plugin", "statusline"]);
        await Assert.That(result.Error).Contains("plugin");
        await Assert.That(account.WiringFailure).Contains("plugin");
        await Assert.That(account.WiredAt).IsNull();
        await Assert.That(capture.GetCapturedError()).Contains("Run `kcap accounts rewire`");
    }

    [Test]
    public async Task Json_flag_prints_one_result_line_with_steps_and_revision() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts", "add", "claude", dir, "--json"]);

        var result   = SingleResult(capture.GetCapturedOutput());
        var registry = Store.Load();
        var account  = registry.Accounts.Single();
        await Assert.That(exit).IsEqualTo(0).Because(capture.GetCapturedError());
        await Assert.That(result.Action).IsEqualTo("add");
        await Assert.That(result.AccountId).IsEqualTo(account.Id);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Revision).IsEqualTo(registry.Revision);
        await Assert.That(result.Steps.Select(s => (s.Name, s.Succeeded))).IsEquivalentTo([("plugin", true), ("statusline", true), ("identity", true)]);
        await Assert.That(account.WiredAt).IsNotNull();
        await Assert.That(account.WiringFailure).IsNull();
        await Assert.That(capture.GetCapturedOutput()).Contains("\"account_id\":").And.Contains("\"revision\":");
    }

    [Test]
    public async Task Remove_reports_the_revision_of_the_removal() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        var revision = Store.Load().Revision;
        var id       = Store.Load().Accounts.Single().Id;
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "remove", dir, "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(result.Action).IsEqualTo("remove");
        await Assert.That(result.AccountId).IsEqualTo(id);
        await Assert.That(result.Succeeded).IsTrue();
        // Unwiring discards the status line backup before the removal commits.
        await Assert.That(result.Revision).IsEqualTo(revision + 2);
        await Assert.That(Store.Load().Revision).IsEqualTo(result.Revision);
    }

    [Test]
    public async Task Remove_whose_unwiring_fails_keeps_the_entry_and_commits_the_failure() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        var before = Store.Load();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "not json");
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts", "remove", dir, "--json"]);

        var result  = SingleResult(capture.GetCapturedOutput());
        var after   = Store.Load();
        var account = after.Accounts.Single();
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(result.Action).IsEqualTo("remove");
        await Assert.That(result.AccountId).IsEqualTo(account.Id);
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Steps.Where(s => !s.Succeeded).Select(s => s.Name)).IsEquivalentTo(["plugin", "statusline"]);
        await Assert.That(result.Error).Contains("It stays registered.");
        await Assert.That(result.Revision).IsEqualTo(before.Revision + 1);
        await Assert.That(after.Revision).IsEqualTo(result.Revision);
        await Assert.That(account.WiringFailure).Contains("plugin");
        await Assert.That(account.WiredAt).IsEqualTo(before.Accounts.Single().WiredAt);
        await Assert.That(capture.GetCapturedError()).Contains("It stays registered.");
    }

    [Test]
    public async Task Json_flag_reports_a_refused_action_on_stdout_and_stderr() {
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts", "rename", "zzzzzzzz", "Work", "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(result.Action).IsEqualTo("rename");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Revision).IsNull();
        await Assert.That(result.Error).Contains("No account matches zzzzzzzz");
        await Assert.That(capture.GetCapturedError()).Contains("No account matches zzzzzzzz");
    }

    [Test]
    public async Task Rename_reports_the_revision_it_committed() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "rename", dir, "Work", "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(result.Steps.Length).IsEqualTo(0);
        await Assert.That(result.Revision).IsEqualTo(Store.Load().Revision);
    }

    [Test]
    public async Task Rewire_of_every_account_reports_one_result_covering_all_of_them() {
        var first  = ClaudeDir();
        var second = ClaudeDir(".claude-other");
        await Quietly("accounts", "add", "claude", first);
        await Quietly("accounts", "add", "claude", second);
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "rewire", "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(result.AccountId).IsNull();
        await Assert.That(result.Steps.Length).IsEqualTo(4);
        await Assert.That(result.Revision).IsEqualTo(Store.Load().Revision);
    }

    [Test]
    public async Task Refresh_json_reports_steps_and_revision() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        var before = Store.Load().Revision;
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "refresh", dir, "--json"]);

        var result  = SingleResult(capture.GetCapturedOutput());
        var account = Store.Load().Accounts.Single();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(result.Action).IsEqualTo("refresh");
        await Assert.That(result.AccountId).IsEqualTo(account.Id);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Steps.Select(s => (s.Name, s.Succeeded))).IsEquivalentTo([("identity", true)]);
        await Assert.That(result.Revision!.Value).IsGreaterThan(before);
        await Assert.That(result.Revision).IsEqualTo(Store.Load().Revision);
        await Assert.That(account.Email).IsEqualTo("me@example.com");
        await Assert.That(account.SignIn).IsEqualTo(SignInStates.SignedIn);
    }

    [Test]
    public async Task Refresh_of_every_account_prints_a_line_each_and_exits_1_when_a_step_failed() {
        await Quietly("accounts", "add", "claude", ClaudeDir());
        await Quietly("accounts", "add", "claude", ClaudeDir(".claude-other"));
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut(StubAccountProbe.Failing).HandleAsync(["accounts", "refresh"]);

        var lines = capture.GetCapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines.All(l => l.Contains("claude not found on PATH"))).IsTrue();
        await Assert.That(Store.Load().Accounts.All(a => a.RefreshError == "claude not found on PATH")).IsTrue();
    }

    [Test]
    public async Task Refresh_text_names_the_sign_in_and_email() {
        var dir = ClaudeDir();
        await Quietly("accounts", "add", "claude", dir);
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "refresh", dir]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).Contains("signed in").And.Contains("me@example.com");
    }

    [Test]
    public async Task Add_result_carries_the_refresh_revision() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();

        await Sut().HandleAsync(["accounts", "add", "claude", dir, "--json"]);

        var result  = SingleResult(capture.GetCapturedOutput());
        var account = Store.Load().Accounts.Single();
        await Assert.That(result.Revision).IsEqualTo(Store.Load().Revision);
        await Assert.That(account.RefreshedAt).IsNotNull();
        await Assert.That(account.Label).IsEqualTo("me@example.com");
    }

    [Test]
    public async Task Add_whose_refresh_fails_still_succeeds_and_warns() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut(StubAccountProbe.Failing).HandleAsync(["accounts", "add", "claude", dir, "--json"]);

        var result = SingleResult(capture.GetCapturedOutput());
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Steps.Single(s => s.Name == "identity").Succeeded).IsFalse();
        await Assert.That(capture.GetCapturedError()).Contains("Warning").And.Contains("claude not found on PATH");
    }

    [Test]
    public async Task Unknown_subcommand_prints_usage_and_exits_1() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Sut().HandleAsync(["accounts", "bogus"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("kcap accounts");
    }

    [Test]
    public async Task Rewire_with_no_accounts_says_so_and_exits_0() {
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "rewire"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).Contains("No accounts registered");
    }

    [Test]
    public async Task Add_codex_enables_sandbox_network_access_for_every_profile_server() {
        var dir = ClaudeDir(".codex-b");
        var env = TestPluginEnvironment.For(Home, PluginDir()) with {
            Profiles = new ProfileConfig { Profiles = new() { ["work"] = new() { ServerUrl = "https://cap.example.test" } } },
        };
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await new AccountsCommand(Store, env, TimeProvider.System).HandleAsync(["accounts", "add", "codex", dir]);

        await Assert.That(exit).IsEqualTo(0).Because(capture.GetCapturedError());
        await Assert.That(File.ReadAllText(Path.Combine(dir, "config.toml"))).Contains("cap.example.test");
    }

    [Test]
    public async Task Tilde_paths_resolve_to_the_home_directory() {
        var dir = ClaudeDir();
        using var capture = ConsoleOutput.StartCapture();
        await Sut().HandleAsync(["accounts", "add", "claude", "~/.claude-work"]);

        var exit = await Sut().HandleAsync(["accounts", "remove", "~/.claude-work"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Store.Load().Accounts.Count).IsEqualTo(0);
        await Assert.That(Directory.Exists(dir)).IsTrue();
    }

    [Test]
    public async Task List_shows_an_id_shorter_than_the_display_prefix() {
        var dir = ClaudeDir();
        Tmp.CreateFile("accounts/accounts.json", $$"""
            { "accounts": [ { "id": "abc", "vendor": "Claude", "directory": {{System.Text.Json.JsonSerializer.Serialize(dir)}}, "label": "work", "added_at": "2026-01-01T00:00:00Z" } ] }
            """);
        using var capture = ConsoleOutput.StartCapture();

        var exit = await Sut().HandleAsync(["accounts", "list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).Contains("[abc]");
    }

    [Test]
    [Arguments("add")]
    [Arguments("rewire")]
    public async Task Skip_codex_network_access_leaves_the_sandbox_network_alone(string verb) {
        var dir = ClaudeDir(".codex-b");
        var env = TestPluginEnvironment.For(Home, PluginDir()) with {
            Profiles = new ProfileConfig { Profiles = new() { ["work"] = new() { ServerUrl = "https://cap.example.test" } } },
        };
        using var capture = ConsoleOutput.StartFullCapture();
        if (verb == "rewire")
            await new AccountsCommand(Store, env, TimeProvider.System).HandleAsync(["accounts", "add", "codex", dir, "--skip-codex-network-access"]);

        string[] args = verb == "add"
            ? ["accounts", "add", "codex", dir, "--skip-codex-network-access"]
            : ["accounts", "rewire", "--skip-codex-network-access"];
        var exit = await new AccountsCommand(Store, env, TimeProvider.System).HandleAsync(args);

        await Assert.That(exit).IsEqualTo(0).Because(capture.GetCapturedError());
        await Assert.That(File.Exists(Path.Combine(dir, "hooks.json"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(dir, "config.toml"))).DoesNotContain("cap.example.test");
    }

    [Test]
    public async Task List_skips_a_hand_edited_entry_whose_directory_is_not_a_valid_path() {
        Tmp.CreateFile("accounts/accounts.json", """
            { "version": 1, "revision": 1, "accounts": [
                { "id": "nul", "vendor": "Claude", "directory": "/h/bad\u0000dir", "label": "bad", "added_at": "2026-01-01T00:00:00Z" }
            ] }
            """);
        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await Sut().HandleAsync(["accounts"]);

        await Assert.That(exit).IsEqualTo(0).Because(capture.GetCapturedError());
    }
}
