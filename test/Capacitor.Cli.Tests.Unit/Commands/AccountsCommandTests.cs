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

    AccountsCommand Sut() => new(Store, TestPluginEnvironment.For(Home, PluginDir()), TimeProvider.System);

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
