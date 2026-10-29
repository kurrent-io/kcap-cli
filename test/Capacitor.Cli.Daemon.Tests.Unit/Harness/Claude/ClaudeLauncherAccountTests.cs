using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// The environment's layout is <c>~/.claude</c> under the temp home and the account is a second
/// directory beside it, each staged so a launch keyed on the wrong one writes visibly there.
public class ClaudeLauncherAccountTests {
    [TempHome] public required TempHome Home { get; init; }

    ClaudePaths EnvironmentLayout => new(Home, null);

    ClaudeLauncher NewLauncher(bool configHome) {
        var config = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        if (configHome) config.Home = Home;

        return new(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
    }

    static LauncherContext Ctx(string source, string worktree, ResolvedAccount? account, bool isReviewFlow = false) =>
        new(
            AgentId:        "a-1",
            SourceRepoPath: source,
            Worktree:       new WorktreeInfo(Path: worktree, Branch: "wt-branch", SourceRepo: source),
            Prompt:         "build it",
            Model:          null,
            Effort:         null,
            Tools:          null,
            IsReview:       false,
            IsReviewFlow:   isReviewFlow,
            Review:         null,
            ReviewLaunch:   null,
            Account:        account
        );

    /// A layout whose <c>.claude.json</c> registers <paramref name="server"/> for the source repo, with
    /// the source's project directory present for the worktree's to link to.
    static void Stage(ClaudePaths layout, string source, string server) {
        Directory.CreateDirectory(layout.ProjectDir(source));
        File.WriteAllText(layout.UserConfigJson, new JsonObject {
            ["projects"] = new JsonObject {
                [source] = new JsonObject {
                    ["mcpServers"] = new JsonObject { [server] = new JsonObject { ["command"] = server } }
                }
            }
        }.ToJsonString());
    }

    static bool Trusted(ClaudePaths layout, string worktree) =>
        JsonNode.Parse(File.ReadAllText(layout.UserConfigJson))?["projects"]?[ClaudeLauncher.NormalizeClaudeProjectKey(worktree)]
            ?["hasTrustDialogAccepted"]?.GetValue<bool>() == true;

    static JsonObject McpServers(string worktree) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(worktree, ".mcp.json")))!["mcpServers"]!.AsObject();

    /// A review-flow launch, so the bypass-permissions consent is written as well.
    [Test]
    public async Task Prepare_writes_trust_and_mcp_config_under_the_account_directory() {
        var source     = Home.CreateDir("repo");
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("claude-work");
        var account    = AccountLayouts.Claude(Home, accountDir);
        Stage(EnvironmentLayout, source, "env-server");
        Stage(account, source, "account-server");
        var environmentConfig = File.ReadAllText(EnvironmentLayout.UserConfigJson);

        NewLauncher(configHome: true).Prepare(
            Ctx(source, worktree, new ResolvedAccount(accountDir, "acct-1", "Work"), isReviewFlow: true));

        await Assert.That(Trusted(account, worktree)).IsTrue();
        await Assert.That(McpServers(worktree).ContainsKey("account-server")).IsTrue();
        await Assert.That(McpServers(worktree).ContainsKey("env-server")).IsFalse();
        await Assert.That(new DirectoryInfo(account.ProjectDir(worktree)).LinkTarget).IsNotNull();
        await Assert.That(File.Exists(account.UserSettings)).IsTrue();

        await Assert.That(File.ReadAllText(EnvironmentLayout.UserConfigJson)).IsEqualTo(environmentConfig);
        await Assert.That(Path.Exists(EnvironmentLayout.ProjectDir(worktree))).IsFalse();
        await Assert.That(File.Exists(EnvironmentLayout.UserSettings)).IsFalse();
    }

    /// The config carries no home: a launch without an account must not need one.
    [Test]
    public async Task Prepare_with_no_account_uses_the_environment_layout() {
        var source   = Home.CreateDir("repo");
        var worktree = Home.CreateDir("wt");
        Stage(EnvironmentLayout, source, "env-server");

        NewLauncher(configHome: false).Prepare(Ctx(source, worktree, account: null));

        await Assert.That(Trusted(EnvironmentLayout, worktree)).IsTrue();
        await Assert.That(McpServers(worktree).ContainsKey("env-server")).IsTrue();
        await Assert.That(new DirectoryInfo(EnvironmentLayout.ProjectDir(worktree)).LinkTarget).IsNotNull();
    }

    [Test]
    public async Task Teardown_removes_the_project_symlink_from_the_account_directory() {
        var source     = Home.CreateDir("repo");
        var worktree   = Home.CreateDir("wt");
        var accountDir = Home.CreateDir("claude-work");
        var account    = AccountLayouts.Claude(Home, accountDir);
        var resolved   = new ResolvedAccount(accountDir, "acct-1", "Work");
        Stage(account, source, "account-server");
        var launcher = NewLauncher(configHome: true);
        launcher.Prepare(Ctx(source, worktree, resolved));
        await Assert.That(new DirectoryInfo(account.ProjectDir(worktree)).LinkTarget).IsNotNull();

        launcher.Cleanup(new AgentInstance(
            "a-1", "build it", null, null, source, "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess(), TimeProvider.System),
            new WorktreeInfo(worktree, "wt-branch", source), new CancellationTokenSource(), resolved
        ) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow
        });

        await Assert.That(Path.Exists(account.ProjectDir(worktree))).IsFalse();
        await Assert.That(Directory.Exists(account.ProjectDir(source))).IsTrue();
    }
}
