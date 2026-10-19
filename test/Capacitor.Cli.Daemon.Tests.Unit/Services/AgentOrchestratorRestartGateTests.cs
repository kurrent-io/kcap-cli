using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The orchestrator's three restart-gate readings: launches in admission, flow participants
/// mid-round, and whether every interactive agent sits at a turn boundary.</summary>
public class AgentOrchestratorRestartGateTests {
    sealed class ObservingFactory : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new();
        public string CliPath => _inner.CliPath;
        public string Vendor => _inner.Vendor;
        public bool SupportsUnattended => false;
        public Func<int>? Observe { get; set; }
        public int ObservedAtStart { get; private set; } = -1;
        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ObservedAtStart = Observe?.Invoke() ?? -1;
            return _inner.StartAsync(ctx, ct);
        }
    }

    static AgentOrchestrator Build(IHostedAgentRuntimeFactory? factory = null, string? repo = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repo, extraRuntimeFactories: factory is null ? null : [factory]);

    [Test]
    public async Task A_launch_is_in_admission_while_the_runtime_starts_and_not_after_its_ledger_write() {
        using var repoPath = GitRepo.CreateWithCommit();
        var factory = new ObservingFactory();
        await using var orch = Build(factory, repoPath);
        factory.Observe = () => orch.LaunchesInAdmission;

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("gate-launch", repoPath));

        await Assert.That(factory.ObservedAtStart).IsEqualTo(1);
        await Assert.That(orch.LaunchesInAdmission).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_launch_leaves_no_launch_in_admission() {
        using var repoPath = GitRepo.CreateWithCommit();
        await using var orch = Build(new ObservingFactory(), repoPath);
        orch.Admission.TryAcquire(static () => false, out var hold);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("gate-refused", repoPath));
        hold!.Abort();

        await Assert.That(orch.LaunchesInAdmission).IsEqualTo(0);
    }

    [Test]
    public async Task Only_running_non_interactive_agents_mid_turn_count_as_flow_participants_mid_round() {
        await using var orch = Build();
        orch.SeedAgentForTest("flow-mid", LaunchKind.ReviewFlow, flowRunId: "r", flowRole: "a").ActivityClock.SetTurnInFlight(true);
        orch.SeedAgentForTest("flow-idle", LaunchKind.ReviewFlow, flowRunId: "r", flowRole: "b");
        orch.SeedAgentForTest("flow-stopped", LaunchKind.ReviewFlow, status: "Stopped", flowRunId: "r", flowRole: "c")
            .ActivityClock.SetTurnInFlight(true);
        orch.SeedAgentForTest("interactive-mid").ActivityClock.SetTurnInFlight(true);

        await Assert.That(orch.FlowParticipantsMidRound).IsEqualTo(1);
    }

    [Test]
    public async Task Interactive_agents_are_at_a_boundary_only_when_all_await_input() {
        await using var orch = Build();
        await Assert.That(orch.InteractiveAgentsAtTurnBoundary).IsTrue();

        var a = orch.SeedAgentForTest("interactive-a");
        var b = orch.SeedAgentForTest("interactive-b");
        orch.SeedAgentForTest("flow-busy", LaunchKind.ReviewFlow, flowRunId: "r", flowRole: "x").ActivityClock.SetTurnInFlight(true);

        a.ActivityClock.SetAwaitingInput(true);
        await Assert.That(orch.InteractiveAgentsAtTurnBoundary).IsFalse();

        b.ActivityClock.SetAwaitingInput(true);
        await Assert.That(orch.InteractiveAgentsAtTurnBoundary).IsTrue();
    }
}
