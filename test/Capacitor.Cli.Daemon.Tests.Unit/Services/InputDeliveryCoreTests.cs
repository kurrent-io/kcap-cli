using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The delivery core both input callers share. It answers with a typed outcome and does two things
/// less than the handler around it: it never reports a drop to the server and never stops an agent,
/// so a caller with its own sender to answer can reuse the same write path without inheriting the
/// server-origin handler's replies.
/// </summary>
public class InputDeliveryCoreTests {
    static AgentOrchestrator Build(CaptureServerConnection server) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    /// <summary>A real Antigravity runtime whose pending-turns queue is genuinely full: capacity 1,
    /// a turn that never ends, one message executing and one queued behind it. The conversation-id
    /// barrier is load-bearing — without it the second send races the worker's own dequeue and the
    /// refusal this class is about lands on the wrong message.</summary>
    static async Task<AntigravityHostedAgentRuntime> FullQueueRuntime() {
        var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.SendUserInputAsync("first");
        await rt.WaitForConversationIdAsync(CancellationToken.None);
        await rt.SendUserInputAsync("queued");

        return rt;
    }

    static async Task WaitForExit(FakeAcpRuntime runtime) {
        for (var i = 0; i < 300; i++) {
            if (runtime.HasExited) return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for the quit command to stop the agent.");
    }

    [Test]
    public async Task A_refused_turn_maps_to_queue_full_and_the_server_caller_reports_it() {
        var server = new CaptureServerConnection();
        await using var rt   = await FullQueueRuntime();
        await using var orch = Build(server);

        var clock = new AgentActivityClock(new FakeTimeProvider());
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "agy-full", rt, activityClock: clock);
        clock.SetAwaitingInput(true);
        var before = clock.ActivitySeq;

        var outcome = await orch.DeliverInputAsync(agent, "refused", null);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.QueueFull);
        // Nothing was handed over, so the agent is no less idle and still needs the person who typed.
        await Assert.That(clock.ActivitySeq).IsEqualTo(before);
        await Assert.That(clock.AwaitingInput).IsTrue();

        var dispatch = Guid.NewGuid();
        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "refused-again", null, dispatch));

        await Assert.That(server.InputRejections)
            .Contains((dispatch, agent.Id, AgentOrchestrator.SendInputDropReason.QueueFull));
    }

    /// <summary>A borrowed round waits for the write to actually land rather than for the queue to
    /// accept it — the snapshot under the runtime is refreshed per round, so "queued" is not enough
    /// to know the round is reading the generation this delivery published.</summary>
    [Test]
    public async Task A_borrowed_round_delivers_through_the_wait_for_write_path() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt    = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "acp-borrowed", rt);

        var outcome = await orch.DeliverInputAsync(agent, "hi", null);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Delivered);
        await Assert.That(rt.WaitForWriteInputs).Contains("hi");
        await Assert.That(rt.Inputs).IsEmpty();
    }

    [Test]
    public async Task A_borrowed_round_on_a_full_queue_still_maps_to_queue_full() {
        var server = new CaptureServerConnection();
        await using var rt   = await FullQueueRuntime();
        await using var orch = Build(server);
        var agent = AgentOrchestratorHarness.SeedBorrowedAcpAgent(orch, "agy-borrowed", rt);

        var outcome = await orch.DeliverInputAsync(agent, "refused", null);

        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.QueueFull);
    }

    [Test]
    public async Task Quit_on_a_non_pty_runtime_is_reported_not_delivered_and_the_server_caller_stops_the_agent() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        var rt    = new FakeAcpRuntime();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-quit", rt);

        var outcome = await orch.DeliverInputAsync(agent, "/quit", null);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.QuitRequested);
        await Assert.That(rt.HasExited).IsFalse();
        await Assert.That(rt.Inputs).IsEmpty();

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "/exit", null));

        await WaitForExit(rt);
    }

    [Test]
    public async Task A_runtime_fault_maps_to_delivery_failed_with_the_runtimes_own_message() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt    = new FakeAcpRuntime { SendUserInputThrow = new IOException("pipe closed") };
        var clock = new AgentActivityClock(new FakeTimeProvider());
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-fault", rt, activityClock: clock);

        var outcome = await orch.DeliverInputAsync(agent, "hi", null);

        await Assert.That(outcome.Kind).IsEqualTo(InputDeliveryKind.Dropped);
        await Assert.That(outcome.Reason).IsEqualTo(AgentOrchestrator.SendInputDropReason.DeliveryFailed);
        await Assert.That(outcome.Error).IsEqualTo("pipe closed");
        await Assert.That(clock.ActivitySeq).IsEqualTo(1UL);
    }

    /// <summary>The outcome describes a write that has finished, not one that has been started: a
    /// caller rendering "sent" off an early return would be reporting a message the runtime has not
    /// taken yet.</summary>
    [Test]
    public async Task Delivery_completes_only_when_the_runtimes_own_write_settles() {
        var server = new CaptureServerConnection();
        await using var orch = Build(server);
        var rt = new FakeAcpRuntime {
            SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "acp-slow", rt);

        var pending = orch.DeliverInputAsync(agent, "hi", null);
        await Task.Delay(100);

        await Assert.That(pending.IsCompleted).IsFalse();

        rt.SendUserInputGate!.SetResult();

        await Assert.That((await pending).Kind).IsEqualTo(InputDeliveryKind.Delivered);
        await Assert.That(rt.Inputs).Contains("hi");
    }
}
