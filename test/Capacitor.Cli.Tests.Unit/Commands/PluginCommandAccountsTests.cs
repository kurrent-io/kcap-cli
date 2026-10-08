using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandAccountsTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    WorkingDirectory Workdir => new(Tmp.CreateDir("work"));

    string PluginDir {
        get {
            var plugin = Tmp.CreateDir("plugin");
            foreach (var name in AgentsSkillsInstaller.SourceNames)
                plugin.CreateFile(Path.Combine("skills", name, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
            return plugin;
        }
    }

    PluginEnvironment Env() {
        var plugin = PluginDir;
        return new(
            Home:              Home,
            Profiles:          new ProfileConfig(),
            ResolvePluginPath: () => plugin,
            Stdout:            new StringWriter(),
            Stderr:            new StringWriter()
        ) {
            Harnesses            = TestHarnesses.Under(Home),
            Binaries             = TestBinaries.None,
            ResolveMcpBinaryPath = () => "/usr/local/bin/kcap",
            Accounts             = new AccountStore(Tmp.PathTo("accounts")),
        };
    }

    [Test]
    public async Task User_install_wires_every_registered_claude_account() {
        var env = Env();
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        Directory.CreateDirectory(Home.PathTo(".claude-work"));

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsTrue();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Install_on_an_empty_registry_adopts_the_default_directory() {
        var env = Env();

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_of_a_pre_registry_install_adopts_and_refreshes() {
        var env = Env();
        ClaudePluginWriter.Install(Home.PathTo(".claude", "settings.json"), PluginDir);
        File.WriteAllText(Home.PathTo(".claude", ClaudePluginInstaller.MarkerFileName), "0.0.1");

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(1);
        await Assert.That(ClaudePluginInstaller.ReadMarker(Home.PathTo(".claude", "settings.json"))).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Refresh_with_a_current_default_marker_still_adopts_and_refreshes_installed_accounts() {
        var env = Env();
        ClaudePluginWriter.Install(Home.PathTo(".claude", "settings.json"), PluginDir);
        ClaudePluginWriter.Install(Home.PathTo(".claude-work", "settings.json"), PluginDir);
        File.WriteAllText(Home.PathTo(".claude-work", ClaudePluginInstaller.MarkerFileName), "0.0.1");
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(2);
        await Assert.That(ClaudePluginInstaller.ReadMarker(Home.PathTo(".claude-work", "settings.json"))).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Refresh_does_not_adopt_a_vendor_kcap_was_never_installed_into() {
        var env = Env();

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Claude).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Project_remove_leaves_registered_accounts_wired() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove", "--project"]);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsTrue();
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Install_with_a_corrupt_registry_warns_and_installs_the_default() {
        var env = Env();
        Tmp.CreateFile(Path.Combine("accounts", "accounts.json"), "{ not json");

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(env.Stderr.ToString()).Contains("Could not read the kcap account registry");
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsTrue();
    }

    [Test]
    public async Task Refresh_with_a_corrupt_registry_still_refreshes_the_default() {
        var env = Env();
        ClaudePluginWriter.Install(Home.PathTo(".claude", "settings.json"), PluginDir);
        File.WriteAllText(Home.PathTo(".claude", ClaudePluginInstaller.MarkerFileName), "0.0.1");
        Tmp.CreateFile(Path.Combine("accounts", "accounts.json"), "{ not json");

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.ReadMarker(Home.PathTo(".claude", "settings.json"))).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task User_remove_with_a_corrupt_registry_still_removes_the_default() {
        var env = Env();
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);
        File.WriteAllText(Tmp.PathTo("accounts", "accounts.json"), "{ not json");

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Refresh_does_not_rewire_an_account_the_user_unwired() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        var work = AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);
        AccountWiring.Unwire(work, Home);
        File.WriteAllText(Home.PathTo(".claude", ClaudePluginInstaller.MarkerFileName), "0.0.1");

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(ClaudePluginInstaller.ReadMarker(Home.PathTo(".claude", "settings.json"))).IsEqualTo(CapacitorVersion.Current());
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Codex_refresh_heals_mcp_for_an_installed_account_with_a_current_marker() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".codex-b"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Codex, Home.PathTo(".codex-b"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--skip-codex-network-access"]);
        var config = Home.PathTo(".codex-b", "config.toml");
        var server = KcapMcpServers.ForCodex[0].Name;
        CodexConfigToml.UnregisterKcapMcpServers(config);
        await Assert.That(File.ReadAllText(config)).DoesNotContain(server);
        await Assert.That(CodexHooksInstaller.ReadMarker(Home.PathTo(".codex-b", "hooks.json"))).IsEqualTo(CapacitorVersion.Current());

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(config)).Contains(server);
    }

    [Test]
    public async Task User_remove_unwires_every_registered_claude_account() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task User_remove_continues_past_a_deleted_account_directory() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-gone"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-gone"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);
        Directory.Delete(Home.PathTo(".claude-gone"), recursive: true);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude", "settings.json"))).IsFalse();
    }

    [Test]
    public async Task Codex_user_install_wires_every_registered_home() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".codex-b"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Codex, Home.PathTo(".codex-b"), TimeProvider.System);

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--skip-codex-network-access"]);

        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex", "hooks.json"))).IsTrue();
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsTrue();
    }

    [Test]
    public async Task Codex_user_remove_unwires_every_registered_home() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".codex-b"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Codex, Home.PathTo(".codex-b"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--skip-codex-network-access"]);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove", "--codex"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(CodexHooksInstaller.ReferencesKcapHook(Home.PathTo(".codex-b", "hooks.json"))).IsFalse();
    }

    [Test]
    public async Task Codex_refresh_adopts_an_installed_default_and_refreshes_installed_homes() {
        var env = Env();
        CodexHooksWriter.Install(Home.PathTo(".codex", "hooks.json"));
        CodexHooksWriter.Install(Home.PathTo(".codex-b", "hooks.json"));
        File.WriteAllText(Home.PathTo(".codex-b", CodexHooksInstaller.MarkerFileName), "0.0.1");
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Codex, Home.PathTo(".codex-b"), TimeProvider.System);

        var exit = await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--codex", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(AccountAdoption.Of(env.Accounts!, HarnessId.Codex).Count).IsEqualTo(2);
        await Assert.That(CodexHooksInstaller.ReadMarker(Home.PathTo(".codex-b", "hooks.json"))).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Refresh_does_not_wire_a_registered_account_kcap_was_never_installed_into() {
        var env = Env();
        ClaudePluginWriter.Install(Home.PathTo(".claude", "settings.json"), PluginDir);
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);

        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(Home.PathTo(".claude-work", "settings.json"))).IsFalse();
    }

    /// <summary>An installed work account with a stale marker, beside a default layout kcap is not installed in, so a
    /// refresh wires only the account and never adopts the default (which would take the lock on its own).</summary>
    async Task<PluginEnvironment> StaleWorkAccountOnly() {
        var env = Env();
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);
        File.Delete(Home.PathTo(".claude", "settings.json"));
        File.Delete(Home.PathTo(".claude", ClaudePluginInstaller.MarkerFileName));
        File.WriteAllText(Home.PathTo(".claude-work", ClaudePluginInstaller.MarkerFileName), "0.0.1");
        return env;
    }

    [Test]
    public async Task Refresh_wires_an_account_only_while_holding_the_registry_lock() {
        var env  = await StaleWorkAccountOnly();
        var work = Home.PathTo(".claude-work", "settings.json");

        Task<int> run;
        using (env.Accounts!.Lock()) {
            run = Task.Run(() => new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]));
            await Task.Delay(TimeSpan.FromSeconds(1));

            await Assert.That(ClaudePluginInstaller.ReadMarker(work)).IsEqualTo("0.0.1");
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.ReadMarker(work)).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task User_remove_unwires_an_account_only_while_holding_the_registry_lock() {
        var env  = Env();
        var work = Home.PathTo(".claude-work", "settings.json");
        Directory.CreateDirectory(Home.PathTo(".claude-work"));
        AccountAdoption.EnsureDefault(env.Accounts!, HarnessId.Claude, Home.PathTo(".claude-work"), TimeProvider.System);
        await new PluginCommand(env, Workdir).HandleAsync(["plugin", "install"]);

        Task<int> run;
        using (env.Accounts!.Lock()) {
            run = Task.Run(() => new PluginCommand(env, Workdir).HandleAsync(["plugin", "remove"]));
            await Task.Delay(TimeSpan.FromSeconds(1));

            await Assert.That(ClaudePluginInstaller.IsPluginEnabled(work)).IsTrue();
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(ClaudePluginInstaller.IsPluginEnabled(work)).IsFalse();
    }

    [Test]
    public async Task Refresh_warns_and_succeeds_when_the_registry_lock_stays_held() {
        var env  = await StaleWorkAccountOnly();
        var work = Home.PathTo(".claude-work", "settings.json");

        int exit;
        using (env.Accounts!.Lock())
            exit = await Task.Run(() => new PluginCommand(env, Workdir).HandleAsync(["plugin", "install", "--if-installed"]));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(env.Stderr.ToString()).Contains("kcap account registry");
        await Assert.That(ClaudePluginInstaller.ReadMarker(work)).IsEqualTo("0.0.1");
    }
}
