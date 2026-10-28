using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// The environment's layout is <c>~/.codex</c> under the temp home and the account is a second
/// directory beside it, so a launch keyed on the wrong one reads or writes visibly there.
public class CodexLauncherAccountTests {
    [TempHome] public required TempHome Home { get; init; }

    const string CapacitorHooks = """
        {"hooks":{
            "SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
            "Stop":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
            "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}]
        }}
        """;

    CodexPaths EnvironmentLayout => new(Home, null);

    CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example", Home = Home },
            TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => []
        };

    static ResolvedAccount Account(string directory) => new(directory, "acct-1", "Work");

    static LauncherContext Ctx(string source, string worktree, ResolvedAccount? account, string model = "gpt-5.3-codex") => new(
        AgentId:        "a-1",
        SourceRepoPath: source,
        Worktree:       new WorktreeInfo(Path: worktree, Branch: "wt-branch", SourceRepo: source),
        Prompt:         null,
        Model:          model,
        Effort:         null,
        Tools:          null,
        IsReview:       false,
        IsReviewFlow:   false,
        Review:         null,
        ReviewLaunch:   null,
        Account:        account
    );

    [Test]
    public async Task Prepare_checks_hooks_in_the_account_directory_not_the_default() {
        var source     = Home.CreateDir("repo");
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("codex-work");
        accountDir.CreateFile("hooks.json", CapacitorHooks);

        await Assert.That(() => NewLauncher().Prepare(Ctx(source, worktree, Account(accountDir)))).ThrowsNothing();
    }

    /// The environment layout carries the hooks, so a launch that consulted it would pass.
    [Test]
    public async Task Prepare_without_account_or_repository_hooks_in_the_account_directory_throws() {
        var source     = Home.CreateDir("repo");
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("codex-work");
        Home.CreateFile([".codex", "hooks.json"], CapacitorHooks);

        await Assert.That(() => NewLauncher().Prepare(Ctx(source, worktree, Account(accountDir))))
            .Throws<CodexHooksNotInstalledException>()
            .WithMessageContaining($"kcap accounts add codex {ShellArgument.Quote(accountDir.Path)}");
    }

    [Test]
    public async Task TrustWorktree_writes_the_account_config_toml() {
        var source     = Home.CreateDir("repo");
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("codex-work");
        accountDir.CreateFile("hooks.json", CapacitorHooks);
        var account = AccountLayouts.Codex(Home, accountDir);

        NewLauncher().Prepare(Ctx(source, worktree, Account(accountDir)));

        var configToml = File.ReadAllText(account.ConfigToml);
        await Assert.That(configToml).Contains($"\"{CodexPaths.NormalizeProjectKey(worktree).Replace("\\", "\\\\")}\"");
        await Assert.That(configToml).Contains("trust_level = \"trusted\"");
        await Assert.That(File.Exists(EnvironmentLayout.ConfigToml)).IsFalse();
    }

    [Test]
    public async Task Model_migrations_are_read_from_the_account_config_toml() {
        var accountDir = Home.CreateDir("codex-work");
        accountDir.CreateFile("config.toml", "[notice.model_migrations]\n\"gpt-5.4-mini\" = \"gpt-5.6-luna\"\n");
        Home.CreateFile([".codex", "config.toml"], "[notice.model_migrations]\n\"gpt-5.4-mini\" = \"gpt-5.5-env\"\n");

        var args = NewLauncher().BuildArgs(Ctx("/tmp/repo", "/tmp/wt", Account(accountDir), model: "gpt-5.4-mini")).Args;

        await Assert.That(args).Contains("gpt-5.6-luna");
        await Assert.That(args).DoesNotContain("gpt-5.5-env");
    }
}
