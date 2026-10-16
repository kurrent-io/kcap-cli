using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorFinalizeTests {
    /// <summary>Pins that a final report the server refuses (a connection closing mid-reconnect) neither
    /// skips the other report nor the agent's local teardown: the session is ended and the agent
    /// unregistered, otherwise its process leaks and it stays listed as live.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_failed_final_report_still_tears_the_agent_down(bool statusFails) {
        var refused = new InvalidOperationException("The server closed the connection");
        var server  = new CaptureServerConnection {
            StatusChangedThrow = statusFails ? refused : null,
            RunEventThrow      = statusFails ? null : refused,
        };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("report-fails", pty: pty);
        pty.Exit();

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(WaitHarness.Bounded);

        await Assert.That(server.RunEvents.Any(static e => e.Event is AgentRunStopped)).IsTrue();
        await Assert.That(server.EndSessionReasons).IsNotEmpty();
        await Assert.That(server.AgentUnregisteredCalls).Contains("report-fails");
    }
}
