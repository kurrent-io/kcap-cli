using Capacitor.Cli.Core.LocalIpc;
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

    /// <summary>The read loop does not wait for a report, so the closing must queue behind an open still in
    /// flight rather than race it to the server.</summary>
    [Test]
    public async Task A_closing_is_not_sent_until_the_open_before_it_is() {
        var server = new CaptureServerConnection { TerminalDialogReportHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("hosted", pty: pty, vendor: "claude");
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit(Dialog);
        await WaitHarness.PollUntilAsync(() => server.TerminalDialogReports.Count == 1);
        var open = agent.TerminalDialogReport;
        pty.Emit(Composer);
        // The chain moves only once the closing is queued, so the count below is read after its attempt.
        await WaitHarness.PollUntilAsync(() => !ReferenceEquals(agent.TerminalDialogReport, open));

        await Assert.That(server.TerminalDialogReports.Count).IsEqualTo(1);

        server.TerminalDialogReportHold.SetResult();
        await WaitHarness.PollUntilAsync(() => server.TerminalDialogReports.Count == 2);
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(server.TerminalDialogReports[1].Dialog).IsNull();
    }

    /// <summary>The dialog closing while its replay is on the way must not leave the server holding it.</summary>
    [Test]
    public async Task A_reconnect_replay_ends_on_the_dialog_the_screen_shows() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);

        var agent = orch.SeedAgentForTest("hosted", vendor: "claude");
        agent.TerminalDialog = new TerminalDialogDto("Allow?", "", ["No", "Yes"], 0, "");
        server.OnTerminalDialogReport = (_, sent) => { if (sent is not null) agent.TerminalDialog = null; };

        await orch.ReRegisterAgentsAsync();

        await Assert.That(server.TerminalDialogReports.Select(r => r.Dialog?.Heading)).IsEquivalentTo(
            new[] { "Allow?", null }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
