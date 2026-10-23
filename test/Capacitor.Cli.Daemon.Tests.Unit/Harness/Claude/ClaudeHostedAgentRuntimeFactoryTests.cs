using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>Which Claude launches leave the PTY: only an interactive, owned-worktree, non-private
/// launch on a daemon set to stream-json, and only when a bridge can answer its prompts.</summary>
public class ClaudeHostedAgentRuntimeFactoryTests {
    [TempHome] public required TempHome Home { get; init; }

    sealed class StubPtyFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "claude";
        public string Vendor => "claude";
        public bool IsAvailable() => true;
        public bool SupportsUnattended => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    sealed class StubLauncher : IHostedAgentLauncher {
        public string Vendor => "claude";
        public string CliPath => "claude";
        public bool SupportsUnattended => true;
        public bool IsAvailable() => true;
        public void Prepare(LauncherContext ctx) { }
        public LaunchArgs BuildArgs(LauncherContext ctx) => new([], null);
        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) => new([], null);
        public void Cleanup(AgentInstance agent) { }
    }

    ClaudeHostedAgentRuntimeFactory Factory(
            string transport, Func<System.Diagnostics.ProcessStartInfo, IJsonLineProcess>? processSource = null) =>
        new(new StubLauncher(), new StubPtyFactory(), new DaemonConfig { ClaudeTransport = transport },
            new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance,
                EphemeralLoopbackPortSource.Instance, TimeProvider.System),
            TestHarnesses.Under(Home).Of<Core.Harness.Claude.ClaudeHarness>().Paths,
            NullLoggerFactory.Instance, TimeProvider.System, processSource);

    static RuntimeStartContext Ctx(
            bool isReview = false, bool isReviewFlow = false, bool isPrivate = false,
            WorkLocation work = WorkLocation.OwnedWorktree, string? bridge = "http://127.0.0.1:1/t",
            string worktree = "/wt") => new(
        AgentId: "agent-1", Vendor: "claude", SourceRepoPath: worktree,
        Worktree: new WorktreeInfo(Path: worktree, Branch: "wt", SourceRepo: worktree),
        Prompt: "hi", Model: null, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "https://t.example", DaemonBridgeUrl: bridge, CapacitorPath: "/opt/kcap",
        Work: work, IsPrivate: isPrivate);

    [Test]
    [Arguments("stream-json",   false, false, false, false, true,  true)]
    [Arguments("pty",           false, false, false, false, true,  false)]
    [Arguments("anything-else", false, false, false, false, true,  false)]
    [Arguments("stream-json",   true,  false, false, false, true,  false)]
    [Arguments("stream-json",   false, true,  false, false, true,  false)]
    [Arguments("stream-json",   false, false, true,  false, true,  false)]
    [Arguments("stream-json",   false, false, false, true,  true,  false)]
    [Arguments("stream-json",   false, false, false, false, false, false)]
    public async Task Routes_only_bridged_interactive_owned_launches_to_stream_json(
            string transport, bool review, bool reviewFlow, bool isPrivate, bool borrowed, bool bridged, bool expected) {
        var ctx = Ctx(review, reviewFlow, isPrivate,
            borrowed ? WorkLocation.BorrowedCwd : WorkLocation.OwnedWorktree, bridged ? "http://127.0.0.1:1/t" : null);

        await Assert.That(Factory(transport).UsesStreamJson(ctx)).IsEqualTo(expected);
    }

    /// <summary>The prompt never reaches the command line: it is the first message after the
    /// handshake.</summary>
    [Test]
    public async Task The_prompt_is_the_first_stdin_message_and_never_an_argument() {
        using var wt = new TempDir();
        var process = new FakeStreamJsonProcess();
        System.Diagnostics.ProcessStartInfo? spawned = null;
        var factory = Factory("stream-json", psi => { spawned = psi; return process; });

        var start = await factory.StartAsync(Ctx(worktree: wt.Path), CancellationToken.None);
        await using var _ = start.Runtime;

        await Assert.That(spawned!.ArgumentList).DoesNotContain("hi");
        await Assert.That(spawned.ArgumentList).Contains("--permission-prompt-tool");
        await Assert.That(spawned.WorkingDirectory).IsEqualTo(wt.Path);
        await Assert.That(spawned.Environment[Core.HostedAgent.AgentIdVar]).IsEqualTo("agent-1");
        await Assert.That(await process.NextWriteAsync()).Contains("\"initialize\"");
        await Assert.That(await process.NextWriteAsync()).Contains("\"content\":\"hi\"");
    }
}
