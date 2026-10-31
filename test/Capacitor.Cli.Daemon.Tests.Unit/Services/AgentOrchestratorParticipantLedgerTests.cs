using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A flow participant whose vendor can reopen its reviewer session gets a resume entry in the
/// participant store, and a keep-agents shutdown stops it for resume with its clock recorded.</summary>
public class AgentOrchestratorParticipantLedgerTests {
    [TempDaemonPaths] public required TempDaemonStore Store { get; init; }

    sealed class ReviewerFactory(bool resumes) : IHostedAgentRuntimeFactory {
        public string CliPath => "unused";
        public string Vendor => "cursor";
        public bool SupportsUnattended => true;
        public bool IsAvailable() => true;
        public bool SupportsReviewerRestartResume(string? runtimeTransport, Core.LocalIpc.WorkLocation work) => resumes;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    AgentOrchestrator Build(bool resumes = true) => AgentOrchestratorHarness.BuildOrchestrator(
        new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
        extraRuntimeFactories: [new ReviewerFactory(resumes)],
        configure: c => {
            c.Store      = Store.Store;
            c.InstanceId = "instance-1";
        });

    static AgentInstance SeedReviewer(AgentOrchestrator orch, string id, string? vendorSession = "vendor-session-1") {
        var agent = AgentOrchestratorHarness.SeedAcpAgent(
            orch, id, new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: true), kind: LaunchKind.ReviewFlow);
        agent.SessionId     = "session-1";
        agent.VendorSession = vendorSession;
        var cmd = new LaunchAgentCommand(
            id, "review this", "default", null, "/repo", ["Read"], null, "cursor", Kind: LaunchKind.ReviewFlow,
            McpAllowlist: ["kcap-flow-result"], FlowRunId: "flow-1", FlowRole: "reviewer",
            ReviewerCertification: new ReviewerCertificationRequirement("cursor", ">=1", "policy-1", "rev-1", "connection-1", "1.2.3"));
        orch.WriteLedger(agent, cmd, origin: "server", consent: null);

        return agent;
    }

    ResumeLedgerStore Participants() => ResumeLedgerStore.ParticipantsFor(Store.Store, "test");

    ResumeLedgerStore Interactive() => ResumeLedgerStore.For(Store.Store, "test");

    [Test]
    public async Task A_resumable_participant_gets_an_entry_in_the_participant_store() {
        var orch = Build();
        SeedReviewer(orch, "reviewer-1");

        var entry = Participants().TryRead("reviewer-1");

        await Assert.That(entry).IsNotNull();
        await Assert.That(Interactive().TryRead("reviewer-1")).IsNull();
        await Assert.That(entry!.Participant!.Kind).IsEqualTo(LaunchKind.ReviewFlow);
        await Assert.That(entry.Participant.McpAllowlist!).IsEquivalentTo(["kcap-flow-result"]);
        await Assert.That(entry.Participant.Tools!).IsEquivalentTo(["Read"]);
        await Assert.That(entry.Participant.Certification!.ExpectedDaemonConnectionId).IsEqualTo("");
        await Assert.That(entry.Participant.Certification.ExpectedCliVersion).IsEqualTo("");
        await Assert.That(entry.Participant.Certification.AllowedCliRanges).IsEqualTo(">=1");
        await orch.DisposeAsync();
    }

    [Test]
    public async Task A_participant_of_a_vendor_that_cannot_resume_gets_no_entry() {
        var orch = Build(resumes: false);
        SeedReviewer(orch, "reviewer-1");

        await Assert.That(Participants().TryRead("reviewer-1")).IsNull();
        await Assert.That(Interactive().TryRead("reviewer-1")).IsNull();
        await orch.DisposeAsync();
    }

    [Test]
    public async Task Delivered_input_is_recorded_as_the_last_round_input() {
        var orch  = Build();
        var agent = SeedReviewer(orch, "reviewer-1");

        var outcome = await orch.DeliverInputAsync(agent, "round 2: check the lock order", null);

        await Assert.That(outcome).IsEqualTo(InputDeliveryOutcome.Delivered);
        var participant = Participants().TryRead("reviewer-1")!.Participant!;
        await Assert.That(participant.LastRoundInput).Contains("round 2: check the lock order");
        await Assert.That(participant.LastRoundAt).IsNotNull();
        await orch.DisposeAsync();
    }

    [Test]
    public async Task A_keep_agents_shutdown_marks_a_participant_resuming_with_its_clock() {
        var orch  = Build();
        var agent = SeedReviewer(orch, "reviewer-1");
        agent.ActivityClock.Advance();
        agent.ActivityClock.Advance();

        await orch.DisposeAsync();

        var entry = Participants().TryRead("reviewer-1")!;
        await Assert.That(entry.State).IsEqualTo(ResumeState.Resuming);
        await Assert.That(entry.Participant!.ActivitySeq).IsGreaterThanOrEqualTo(3UL);
    }

    [Test]
    public async Task A_participant_with_no_vendor_session_is_ended_not_marked() {
        var orch = Build();
        SeedReviewer(orch, "reviewer-1", vendorSession: null);

        await orch.DisposeAsync();

        await Assert.That(Participants().TryRead("reviewer-1")).IsNull();
    }
}
