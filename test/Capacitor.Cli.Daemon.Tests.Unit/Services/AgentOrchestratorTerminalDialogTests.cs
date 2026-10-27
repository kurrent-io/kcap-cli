using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pins the server relay of a Claude dialog read off the terminal: each change is reported,
/// its closing included, and a private agent's never leaves the machine.</summary>
public class AgentOrchestratorTerminalDialogTests {
    const string Dialog =
        "────────────────────────────────────────\r\n" +
        " Allow external CLAUDE.md file imports?\r\n" +
        "\r\n" +
        " ❯ No, disable external imports\r\n" +
        "   Yes, allow external imports\r\n" +
        "\r\n" +
        " Enter to confirm · Esc to cancel\r\n";

    const string Composer = "\x1b[2J\x1b[H> ";

    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task A_dialog_and_its_closing_are_reported_to_the_server() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("hosted", pty: pty, vendor: "claude");
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit(Dialog);
        await WaitHarness.PollUntilAsync(() => server.TerminalDialogReports.Count == 1);
        pty.Emit(Composer);
        await WaitHarness.PollUntilAsync(() => server.TerminalDialogReports.Count == 2);
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        var (agentId, shown) = server.TerminalDialogReports[0];
        await Assert.That(agentId).IsEqualTo("hosted");
        await Assert.That(shown!.Heading).IsEqualTo("Allow external CLAUDE.md file imports?");
        await Assert.That(shown.Options).IsEquivalentTo(new[] { "No, disable external imports", "Yes, allow external imports" });
        await Assert.That(server.TerminalDialogReports[1].Dialog).IsNull();
    }

    [Test]
    public async Task A_private_agent_never_reports_its_dialog() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("private", isPrivate: true, pty: pty, vendor: "claude");
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit(Dialog);
        await WaitHarness.PollUntilAsync(() => agent.TerminalDialog is not null);
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(server.TerminalDialogReports).IsEmpty();
    }
}
