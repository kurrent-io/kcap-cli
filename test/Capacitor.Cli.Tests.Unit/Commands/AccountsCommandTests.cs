using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
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
}
