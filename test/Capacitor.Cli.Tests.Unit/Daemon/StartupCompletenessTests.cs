using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

// Phase B2-b (sequenced-settlement design): the orchestrator surfaces the durable coverage
// boot-chain verdict (DaemonConfig.RecordlessSurvivorsImpossible, folded in DaemonRunner before
// Connect) so the enriched DaemonConnect payload can advertise it. This partial reuses the vendor
// test class's BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory harness.
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Status_report_advertises_recordless_survivors_impossible_from_config() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.RecordlessSurvivorsImpossible = true);

        await Assert.That(orch.RecordlessSurvivorsImpossibleForTest).IsTrue();
    }

    // Phase B2-b (sequenced-settlement design §4.2.4): the ledger's un-acked snapshot is re-reported on
    // every status report until the server prunes it per-entry via AckResolvedCandidates. The seam and
    // the underlying ledger Ack are SYNCHRONOUS (void) — no await (would be CS4008).
    [Test]
    public async Task Status_report_carries_resolved_candidates_and_ack_prunes_them() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var g = orch.SeedResolvedCandidateForTest("a1", "old-epoch");   // test seam over the ledger
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates!.Single().AgentId).IsEqualTo("a1");

        // The seam + the underlying ledger Ack are SYNCHRONOUS (void) — no await (would be CS4008).
        orch.HandleAckResolvedCandidatesForTest(
            new AckResolvedCandidates([new ResolvedCandidateAck(g.Generation, "a1", "old-epoch")]));
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates).IsEmpty();
    }
}
