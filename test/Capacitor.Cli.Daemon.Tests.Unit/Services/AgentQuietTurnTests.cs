using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A running PTY agent whose terminal has gone quiet is waiting, including when the stop notice
/// never stuck. The published status is what the desktop badge reads.
public class AgentQuietTurnTests {
    static AgentOrchestrator Build(TimeProvider time) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);

    static bool? Awaiting(AgentOrchestrator orch, string id) =>
        orch.SnapshotAgentsForStatus().Single(a => a.Id == id).AwaitingInput;

    // Captured when focusing the finished Claude terminal: character-set selection, shift-in,
    // bracketed paste, and mouse-reporting modes. None of these bytes represents new task output.
    const string IdleModeFrame = "\x1b(B\x0f\x1b[?2004h\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Focusing_an_idle_terminal_does_not_publish_working_for_mode_changes(bool fragmented) {
        var time = new FakeTimeProvider();
        await using var orch = Build(time);
        var pty = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("idle-modes", isPrivate: true, pty: pty);
        time.Advance(AgentActivityClock.QuietTurn);
        var reading = orch.ReadAgentOutputForTest(agent);
        try {
            var chunks = fragmented ? IdleModeFrame.Select(c => c.ToString()).ToArray() : [IdleModeFrame];
            foreach (var chunk in chunks) pty.Emit(chunk);
            await WaitHarness.PollUntilAsync(() => agent.OutputBuffer.GetAll().Count == chunks.Length);
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

            pty.Emit("new task output");
            await WaitHarness.PollUntilAsync(() => agent.OutputBuffer.GetAll().Count == chunks.Length + 1);
            await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
        } finally {
            pty.Exit();
            await reading.WaitAsync(WaitHarness.Bounded);
        }
    }

    [Test]
    public async Task Terminal_mode_changes_do_not_extend_a_finished_turns_quiet_window() {
        var time = new FakeTimeProvider();
        await using var orch = Build(time);
        var pty = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("quiet-modes", isPrivate: true, pty: pty);
        var reading = orch.ReadAgentOutputForTest(agent);
        try {
            time.Advance(AgentActivityClock.QuietTurn - TimeSpan.FromSeconds(1));
            pty.Emit(IdleModeFrame);
            await WaitHarness.PollUntilAsync(() => agent.OutputBuffer.GetAll().Count == 1);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
        } finally {
            pty.Exit();
            await reading.WaitAsync(WaitHarness.Bounded);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resizing_an_idle_terminal_keeps_redraw_output_from_publishing_working(bool web) {
        var time = new FakeTimeProvider();
        await using var orch = Build(time);
        var pty = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("idle-resize", isPrivate: !web, pty: pty);
        time.Advance(AgentActivityClock.QuietTurn);
        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

        if (web) orch.HandleResizeTerminalForTest(new ResizeTerminalCommand(agent.Id, 100, 30));
        else {
            using var frames = new MemoryStream();
            await FrameCodec.WriteAsync(frames, LocalFrame.Resize(100, 30), default);
            await FrameCodec.WriteAsync(frames, LocalFrame.Detach(), default);
            frames.Position = 0;
            using var client = new DuplexTestStream(frames, new MemoryStream());
            await orch.HandleLocalAttachAsync(agent.Id, client, default);
        }
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)100);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)30);

        var reading = orch.ReadAgentOutputForTest(agent);
        try {
            pty.Emit("\x1b[2Jredraw");
            await WaitHarness.PollUntilAsync(() => agent.ActivityClock.ActivitySeq == 2);
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

            time.Advance(TimeSpan.FromMilliseconds(100));
            pty.Emit("\x1b[Hprompt");
            await WaitHarness.PollUntilAsync(() => agent.ActivityClock.ActivitySeq == 3);
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
            await WaitHarness.PollUntilAsync(() => agent.OutputBuffer.GetAll().Count == 2);
            await Assert.That(agent.OutputBuffer.Snapshot()).IsEquivalentTo("\x1b[2Jredraw\x1b[Hprompt"u8.ToArray(), CollectionOrdering.Matching);

            // A resize cannot mask future work when the stop notice was only inferred.
            time.Advance(TimeSpan.FromSeconds(2));
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
            pty.Emit("new activity");
            await WaitHarness.PollUntilAsync(() => agent.ActivityClock.ActivitySeq == 4);
            await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
        } finally {
            pty.Exit();
            await reading.WaitAsync(WaitHarness.Bounded);
        }
    }

    /// Claude enables mouse reporting, so scrolling or pointing at its terminal is PTY input, and
    /// Claude repaints its view in reply. Neither the input nor the repaint is a new turn.
    [Test]
    [Arguments("\x1b[<64;20;8M")]
    [Arguments("\x1b[<35;20;8M")]
    [Arguments("a")]
    public async Task Viewer_input_to_an_idle_terminal_keeps_its_repaint_from_publishing_working(string input) {
        var time = new FakeTimeProvider();
        await using var orch = Build(time);
        var pty = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("idle-scroll", isPrivate: true, pty: pty);
        time.Advance(AgentActivityClock.QuietTurn);
        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

        using var frames = new MemoryStream();
        await FrameCodec.WriteAsync(frames, LocalFrame.Stdin(System.Text.Encoding.UTF8.GetBytes(input)), default);
        await FrameCodec.WriteAsync(frames, LocalFrame.Detach(), default);
        frames.Position = 0;
        using var client = new DuplexTestStream(frames, new MemoryStream());
        await orch.HandleLocalAttachAsync(agent.Id, client, default);

        var reading = orch.ReadAgentOutputForTest(agent);
        try {
            pty.Emit("\x1b[2Jscrolled view");
            await WaitHarness.PollUntilAsync(() => agent.ActivityClock.ActivitySeq == 2);
            await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
        } finally {
            pty.Exit();
            await reading.WaitAsync(WaitHarness.Bounded);
        }
    }

    [Test]
    public async Task A_status_snapshot_carries_when_the_current_turn_started() {
        var time = new FakeTimeProvider();
        await using var orch = Build(time);
        var agent = orch.SeedAgentForTest("turn-start", isPrivate: true, pty: new ScriptedPtyProcess());
        agent.ActivityClock.SetAwaitingInput(true);
        await Assert.That(orch.SnapshotAgentsForStatus().Single().WorkingSince).IsNull();

        time.Advance(TimeSpan.FromSeconds(5));
        var started = time.GetUtcNow().UtcDateTime;
        agent.ActivityClock.SetAwaitingInput(false);
        // A tool hook repeats the verdict mid-turn; it must not restart the turn.
        time.Advance(TimeSpan.FromSeconds(3));
        agent.ActivityClock.SetAwaitingInput(false);
        time.Advance(TimeSpan.FromSeconds(3));

        await Assert.That(orch.SnapshotAgentsForStatus().Single().WorkingSince).IsEqualTo(started);
    }

    [Test]
    public async Task A_quiet_pty_agent_is_published_as_waiting_and_output_clears_it() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-1");

        time.Advance(AgentActivityClock.QuietTurn - TimeSpan.FromSeconds(1));
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

        agent.ActivityClock.AdvanceTerminalOutput(hasVisibleText: true);
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
    }

    [Test]
    public async Task A_live_subagent_keeps_the_parent_from_being_marked_quiet() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-2");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", true, time.GetUtcNow().ToUnixTimeMilliseconds());

        time.Advance(AgentActivityClock.QuietTurn);
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
    }

    [Test]
    public async Task A_subagent_stop_marks_a_parent_whose_terminal_is_already_quiet() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-3");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", true, time.GetUtcNow().ToUnixTimeMilliseconds());

        time.Advance(AgentActivityClock.QuietTurn);
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", false, time.GetUtcNow().ToUnixTimeMilliseconds());

        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
    }
}
