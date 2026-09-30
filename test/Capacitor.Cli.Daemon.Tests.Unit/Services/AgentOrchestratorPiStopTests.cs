using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>An owner stop of an interactive Pi agent, driven through a real
/// <see cref="Capacitor.Cli.Daemon.Harness.Pi.PiRpcHostedAgentRuntime"/>.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorPiStopTests {
    /// <summary>Pi exits on its own once stdin closes, so an owner stop ends it cleanly: Completed with
    /// its session, never a kill's exit code and never a Failed that the web UI renders as a launch
    /// failure.</summary>
    [Test]
    public async Task An_owner_stop_lets_pi_exit_cleanly_and_reports_completed() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process) = PiRpcRuntimeFakes.NewRuntime();
        process.ExitsOnInputClose = true;
        await runtime.WaitForSessionReadyAsync(CancellationToken.None);
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "pi-stop-1", runtime, status: "Running");
        agent.SessionId = PiRpcRuntimeFakes.PiSessionId;

        await orch.HandleStopAgentForTest("pi-stop-1");
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(agent.Status).IsEqualTo("Completed");
        await Assert.That(server.StatusChangedWithSession)
            .Contains(("pi-stop-1", "Completed", PiRpcRuntimeFakes.PiSessionId));
        await Assert.That(server.StatusChangedWithSession.Any(c => c.Status == "Failed")).IsFalse();
        await Assert.That(runtime.ExitCode).IsEqualTo(0);
    }
}
