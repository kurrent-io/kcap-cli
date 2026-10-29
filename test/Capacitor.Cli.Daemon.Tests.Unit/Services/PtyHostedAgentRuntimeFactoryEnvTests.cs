using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A daemon started with <c>CLAUDE_CONFIG_DIR</c> set hands every child that value unless the
/// launch says otherwise, so the spawn must name the account's directory or drop the variable.</summary>
[NotInParallel]
public class PtyHostedAgentRuntimeFactoryEnvTests {
    const string Variable = "CLAUDE_CONFIG_DIR";

    [TempHome] public required TempHome Home { get; init; }

    string DaemonDefault => Home.CreateDir(".claude-work");

    /// <summary>The spawn's overlay, and the environment the PTY builds from it over the daemon's own.</summary>
    async Task<(IReadOnlyDictionary<string, string> Overlay, IReadOnlyDictionary<string, string> Child)> ChildEnvFor(ResolvedAccount account) {
        var pty     = new SpyPtyProcessFactory();
        var factory = new PtyHostedAgentRuntimeFactory(
            new SpyHostedAgentLauncher("claude", "claude"), pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance,
            TimeProvider.System, Home);

        var start = await factory.StartAsync(Context(account), CancellationToken.None);
        await start.Runtime.DisposeAsync();

        return (pty.LastEnv!, UnixPtyProcess.BuildChildEnv(pty.LastEnv, pty.LastRemoveEnv, 120, 40));
    }

    RuntimeStartContext Context(ResolvedAccount account) {
        var worktree = Home.CreateDir("wt");

        return new(
            AgentId: "agent-env-1", Vendor: "claude", SourceRepoPath: worktree,
            Worktree: new WorktreeInfo(worktree, "wt", worktree),
            Prompt: null, Model: null, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: false, Review: null,
            Cols: 120, Rows: 40, ServerUrl: "", DaemonBridgeUrl: null, CapacitorPath: "kcap",
            Account: account);
    }

    [Test]
    public async Task Launch_resolved_to_the_built_in_directory_removes_the_inherited_variable() {
        using var env = EnvScope.Exclusive(Variable, DaemonDefault);

        var (_, child) = await ChildEnvFor(new ResolvedAccount(Home.CreateDir(".claude"), "acct-personal", "Personal"));

        await Assert.That(child.ContainsKey(Variable)).IsFalse();
        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo(DaemonDefault);
    }

    [Test]
    public async Task Launch_resolved_to_the_daemons_own_custom_default_sets_it_explicitly() {
        using var env = EnvScope.Exclusive(Variable, DaemonDefault);

        var (overlay, child) = await ChildEnvFor(new ResolvedAccount(DaemonDefault, null, ".claude-work"));

        await Assert.That(overlay[Variable]).IsEqualTo(DaemonDefault);
        await Assert.That(child[Variable]).IsEqualTo(DaemonDefault);
        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo(DaemonDefault);
    }

    [Test]
    public async Task Launch_resolved_to_another_custom_account_sets_that_directory() {
        using var env = EnvScope.Exclusive(Variable, DaemonDefault);
        var other = Home.CreateDir(".claude-other");

        var (_, child) = await ChildEnvFor(new ResolvedAccount(other, "acct-other", "Other"));

        await Assert.That(child[Variable]).IsEqualTo(other);
        await Assert.That(Environment.GetEnvironmentVariable(Variable)).IsEqualTo(DaemonDefault);
    }
}
