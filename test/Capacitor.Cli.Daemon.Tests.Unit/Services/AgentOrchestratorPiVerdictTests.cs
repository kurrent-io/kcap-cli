using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Proves a Pi reviewer's reap verdict reaches the orchestrator through the same
/// <see cref="ITerminationVerdictSource"/> gate <see cref="AgentOrchestratorFinalizerVerdictTests"/>
/// pins for ACP — the finalizer's launch-window report, the stop and reconnect suppressions, the
/// launch path's pre-registration case, and the check-to-send race — driven through a REAL
/// <see cref="PiRpcHostedAgentRuntime"/> rather than a fake <see cref="ITerminationVerdictSource"/>.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorPiVerdictTests {
    /// <summary>A Pi runtime whose review-flow dialog guard has already reaped it — before or after
    /// its first turn settles, per <paramref name="afterFirstSettle"/> — with the verdict published
    /// by the time this returns.</summary>
    static async Task<(PiRpcHostedAgentRuntime Runtime, FakePiRpcProcess Process)> ReapedAsync(bool afterFirstSettle) {
        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime(
            reviewerGuards: new PiReviewerGuards(TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(50)));
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        if (afterFirstSettle) {
            process.Push(PiRpcRuntimeFakes.AgentStart);
            process.Push(PiRpcRuntimeFakes.AgentSettled);
            await runtime.WaitForTurnIdleAsync(CancellationToken.None);
        }

        process.Push(PiRpcRuntimeFakes.DialogRequest("confirm"));
        for (var i = 0; i < 200 && runtime.ReadVerdict() is null; i++) await Task.Delay(10);

        return (runtime, process);
    }

    /// <summary>Stub <see cref="IHostedAgentRuntimeFactory"/> for vendor "pi" that returns a runtime
    /// the test already built, rather than constructing one itself — the real
    /// <see cref="PiRpcHostedAgentRuntimeFactory"/> checks <c>ReadVerdict()</c> before returning from
    /// its own reviewer launch and would never hand the orchestrator an already-reaped runtime, so
    /// this double is the only way to drive <c>HandleLaunchAgent</c>'s post-registration path with
    /// one.</summary>
    sealed class StubPiRuntimeFactory(PiRpcHostedAgentRuntime runtime) : IHostedAgentRuntimeFactory {
        public string CliPath            => "unused-by-this-double";
        public string Vendor             => "pi";
        public bool   SupportsUnattended => false;

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
    }

    /// <summary>A reap inside the launch window reports LaunchFailed with the coded reason and forces
    /// terminal Failed — the ACP shape <c>Report_sent_before_exit_wait</c> pins, through Pi's own
    /// runtime.</summary>
    [Test]
    public async Task A_first_round_reap_is_reported_as_a_launch_failure_with_its_reason() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, _) = await ReapedAsync(afterFirstSettle: false);
        await Assert.That(runtime.ReadVerdict()!.ReapedInsideLaunchWindow).IsTrue();

        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-reap-1", runtime);

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(server.LaunchFailedCalls.Count(c => c.AgentId == "pi-reap-1")).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "pi-reap-1").Reason)
            .Contains("pi_reviewer_unexpected_dialog");
        await Assert.That(agent.Status).IsEqualTo("Failed");
    }

    /// <summary>A reap outside the launch window sends no LaunchFailed and falls through to the
    /// ordinary exit-code-driven teardown — the same shape
    /// <c>Post_window_reap_sends_no_launchfailed_teardown_byte_identical</c> pins for ACP.</summary>
    [Test]
    public async Task A_reap_after_the_first_settle_is_an_ordinary_death() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, _) = await ReapedAsync(afterFirstSettle: true);
        await Assert.That(runtime.ReadVerdict()!.ReapedInsideLaunchWindow).IsFalse();

        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-post-settle-1", runtime);

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(server.LaunchFailedCalls.Any(c => c.AgentId == "pi-post-settle-1")).IsFalse();
        // The fake process's own reap-driven TerminateAsync leaves a non-zero exit code, so the
        // ordinary (verdict-free) exit-code path lands on Failed rather than Completed — still an
        // ordinary death, reported exactly once, with the agent fully unregistered.
        await Assert.That(agent.Status).IsEqualTo("Failed");
        await Assert.That(server.StatusChangedCalls).Contains(("pi-post-settle-1", "Failed"));
        await Assert.That(server.AgentUnregisteredCalls).Contains("pi-post-settle-1");
    }

    /// <summary>A runtime that already carries a launch-window verdict when
    /// <c>HandleLaunchAgent</c> registers it must never have its post-registration Running status
    /// reach the server; the gate suppresses it, and the eventual LaunchFailed still follows once the
    /// agent is finalized.</summary>
    [Test]
    public async Task A_verdict_published_before_registration_never_yields_a_running_status() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();
        var (runtime, _) = await ReapedAsync(afterFirstSettle: false);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [new StubPiRuntimeFactory(runtime)]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "pi-prereg-1",
            Prompt: "go",
            Model: "",
            Effort: null,
            RepoPath: repoPath,
            Tools: null,
            AttachmentIds: null,
            Vendor: "pi"
        ));

        await Assert.That(server.AgentRegisteredCalls.Any(c => c.AgentId == "pi-prereg-1")).IsTrue();
        await Assert.That(server.StatusChangedCalls.Any(
            c => c.AgentId == "pi-prereg-1" && c.Status == "Running")).IsFalse();

        var agent = orch.GetAgentForTest("pi-prereg-1");
        await Assert.That(agent).IsNotNull();

        await orch.FinalizeAgentRunForTest(agent!).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(server.LaunchFailedCalls.Any(c => c.AgentId == "pi-prereg-1")).IsTrue();
    }

    /// <summary>A stop racing (or following) a published Pi verdict must not clear it with a
    /// Completed status.</summary>
    [Test]
    public async Task Stop_sends_no_completed_after_a_pi_verdict() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, _) = await ReapedAsync(afterFirstSettle: false);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-stop-1", runtime, status: "Running");

        await orch.HandleStopAgentForTest("pi-stop-1");

        await Assert.That(server.StatusChangedCalls.Any(
            c => c.AgentId == "pi-stop-1" && c.Status == "Completed")).IsFalse();
    }

    /// <summary>A published Pi verdict excludes the agent from reconnect re-registration entirely —
    /// no status resend, and no re-registration call at all.</summary>
    [Test]
    public async Task Reconnect_sends_no_status_after_a_pi_verdict() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, _) = await ReapedAsync(afterFirstSettle: false);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-rereg-1", runtime, status: "Running");

        await orch.ReRegisterAgentsForTestAsync();

        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "pi-rereg-1")).IsFalse();
        await Assert.That(server.AgentRegisteredCalls.Any(c => c.AgentId == "pi-rereg-1")).IsFalse();
    }

    /// <summary>The stop gate holds its publication lock across the verdict check and the status
    /// send's initiation, so a reap racing right between them cannot land before the send is
    /// initiated — the same atomicity <c>Stop_gate_atomically_serializes_a_publish_against_the_completed_send</c>
    /// pins for ACP.</summary>
    [Test, NotInParallel] // the gate blocks a dedicated thread on a Join up to the bound; keep it off peers
    public async Task A_verdict_cannot_publish_between_the_check_and_a_pi_status_send() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, _) = PiRpcRuntimeFakes.NewRuntime(
            reviewerGuards: new PiReviewerGuards(TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(50)));
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);

        AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-atomic-1", runtime, status: "Running");
        server.VerdictCaptureRuntime = runtime;

        runtime.BeforeGatedSendHookForTest = () => {
            // Dedicated thread, not the pool: the gate holds its lock across the check and the send's
            // initiation, so this reap attempt (a leading "/" is Pi's own synchronous reap trigger)
            // blocks on it post-fix and the Join times out with nothing published; pre-fix it
            // publishes fast and the send follows publication.
            var t = new Thread(() => {
                try { runtime.SendUserInputAsync("/reap-me").GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { /* the guard's own synchronous rejection */ }
            }) { IsBackground = true };
            t.Start();
            t.Join(TimeSpan.FromMilliseconds(500));
        };

        await Task.Factory.StartNew(
            () => orch.HandleStopAgentForTest("pi-atomic-1"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await Assert.That(server.NonFailureStatusSentAfterVerdictPublished).IsFalse();
    }
}
