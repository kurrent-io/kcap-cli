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
}
