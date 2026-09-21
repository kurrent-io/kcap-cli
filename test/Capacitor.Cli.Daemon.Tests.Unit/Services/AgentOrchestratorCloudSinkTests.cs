using System.Text;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the read loop's fan-out against a slow or blocked cloud lane: local sinks and the PTY
/// drain never wait on it, one agent's backlog is its own, and the mirror recovers with a reset.
/// </summary>
public class AgentOrchestratorCloudSinkTests {
    static readonly byte[] ResetBytes = [0x1B, 0x63];

    static readonly CloudTerminalSinkOptions SmallBudget = new() {
        BacklogBudgetBytes = 64,
        RetryDelay         = TimeSpan.FromMilliseconds(5),
        DrainBound         = TimeSpan.FromMilliseconds(100),
        CancellationGrace  = TimeSpan.FromMilliseconds(100),
    };

    static AgentOrchestrator Build(CaptureServerConnection server) {
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.CloudSinkOptions = SmallBudget;

        return orch;
    }

    static RecordingTerminalSink AttachLocal(AgentInstance agent) {
        var sink = new RecordingTerminalSink();
        lock (agent.SinksLock) agent.LocalSinks.Add(sink);

        return sink;
    }

    static string[] MirrorOf(CaptureServerConnection server, string agentId) {
        var screen = new List<string>();

        foreach (var (id, data) in server.TerminalSends) {
            if (id != agentId) continue;

            if (data.AsSpan().SequenceEqual(ResetBytes)) screen.Clear();
            else screen.Add(Encoding.UTF8.GetString(data));
        }

        return [.. screen];
    }

    static int ResetsFor(CaptureServerConnection server, string agentId) =>
        server.TerminalSends.Count(s => s.AgentId == agentId && s.Data.AsSpan().SequenceEqual(ResetBytes));

    [Test]
    public async Task A_blocked_cloud_lane_stops_neither_local_output_nor_the_pty_drain() {
        var open   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection { TerminalSendGate = (_, ct) => open.Task.WaitAsync(ct) };

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("hosted", pty: pty); // server-launched shape: not private
        var local = AttachLocal(agent);
        var loop  = orch.ReadAgentOutputForTest(agent);

        var expected = new StringBuilder();
        for (var i = 0; i < 200; i++) {
            var text = $"line-{i:D4};";
            expected.Append(text);
            pty.Emit(text);
        }

        // 2 KB against a 64-byte cloud budget, with every cloud send blocked: the local sink
        // still receives all of it, which also proves the runtime was drained.
        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 200);
        await Assert.That(string.Concat(local.Chunks)).IsEqualTo(expected.ToString());

        open.SetResult();
        await WaitHarness.PollUntilAsync(() => string.Concat(MirrorOf(server, "hosted")) == expected.ToString());

        // Recovery went through a reset, and at most the one in-flight chunk preceded it.
        var firstReset = server.TerminalSends.ToList().FindIndex(s => s.Data.AsSpan().SequenceEqual(ResetBytes));
        await Assert.That(firstReset).IsGreaterThanOrEqualTo(0);
        await Assert.That(firstReset).IsLessThanOrEqualTo(1);

        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task One_agents_blocked_cloud_lane_does_not_touch_another_agent() {
        var never  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection {
            TerminalSendGate = (agentId, ct) => agentId == "noisy" ? never.Task.WaitAsync(ct) : Task.CompletedTask
        };

        await using var orch = Build(server);

        var noisyPty = new ScriptedPtyProcess();
        var quietPty = new ScriptedPtyProcess();
        var noisy    = orch.SeedAgentForTest("noisy", pty: noisyPty);
        var quiet    = orch.SeedAgentForTest("quiet", pty: quietPty);
        var local    = AttachLocal(quiet);
        var loops    = new[] { orch.ReadAgentOutputForTest(noisy), orch.ReadAgentOutputForTest(quiet) };

        for (var i = 0; i < 200; i++) noisyPty.Emit($"flood-{i:D4};"); // far past noisy's budget
        quietPty.Emit("echo");

        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 1);
        await WaitHarness.PollUntilAsync(() => server.TerminalSends.Any(s => s.AgentId == "quiet"));

        await Assert.That(string.Concat(MirrorOf(server, "quiet"))).IsEqualTo("echo");
        await Assert.That(ResetsFor(server, "quiet")).IsEqualTo(0);

        noisyPty.Exit();
        quietPty.Exit();
        await Task.WhenAll(loops).WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task A_private_agent_never_sends_terminal_output() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("private", isPrivate: true, pty: pty);
        var local = AttachLocal(agent);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("secret");
        await WaitHarness.PollUntilAsync(() => local.Chunks.Count == 1);
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(server.TerminalSendStarts).IsEqualTo(0);
        await Assert.That(agent.CloudSink).IsNull();
    }

    [Test]
    public async Task No_terminal_send_starts_after_the_agent_unregisters() {
        // OnAgentUnregistered is init-only, so the callback reaches the connection through the
        // variable it is being assigned to.
        var startsAtUnregister = -1;
        CaptureServerConnection? server = null;
        server = new CaptureServerConnection {
            TerminalSendGate    = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            OnAgentUnregistered = () => startsAtUnregister = server!.TerminalSendStarts
        };

        await using var orch = Build(server);

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("ending", pty: pty);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("one");
        pty.Emit("two");
        await WaitHarness.PollUntilAsync(() => server.TerminalSendStarts == 1);

        // The read loop ends with the cloud blocked: it must reach finalization within the stop
        // deadline (drain bound + cancellation grace), not wait for the transport.
        pty.Exit();
        await loop.WaitAsync(WaitHarness.Bounded);

        await Assert.That(startsAtUnregister).IsEqualTo(1);
        await Assert.That(server.TerminalSendStarts).IsEqualTo(1);
    }

    [Test]
    public async Task Disposal_cancels_a_gated_send_even_while_its_read_loop_is_draining() {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server    = new CaptureServerConnection {
            TerminalSendGate = async (_, ct) => {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
        };

        // Disposed explicitly below; the run-once guard makes the scope's second dispose a no-op.
        await using var orch = Build(server);
        orch.CloudSinkOptions = SmallBudget with { DrainBound = TimeSpan.FromMinutes(10) };

        var pty   = new ScriptedPtyProcess();
        var agent = orch.SeedAgentForTest("draining", pty: pty);
        var loop  = orch.ReadAgentOutputForTest(agent);

        pty.Emit("stuck");
        await WaitHarness.PollUntilAsync(() => server.TerminalSendStarts == 1);
        pty.Exit(); // the read loop is now inside its own ten-minute StopAsync

        await orch.DisposeAsync().AsTask().WaitAsync(WaitHarness.Bounded);

        await Assert.That(cancelled.Task.IsCompleted).IsTrue();
        await loop.WaitAsync(WaitHarness.Bounded);
    }

    [Test]
    public async Task No_sink_is_admitted_once_disposal_has_begun() {
        var server = new CaptureServerConnection();

        await using var orch = Build(server);
        var agent = orch.SeedAgentForTest("late", pty: new ScriptedPtyProcess());

        await orch.DisposeAsync().AsTask().WaitAsync(WaitHarness.Bounded);

        await Assert.That(orch.TryStartCloudSink(agent)).IsNull();
    }
}
