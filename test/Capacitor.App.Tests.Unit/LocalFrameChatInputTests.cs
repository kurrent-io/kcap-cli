using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class LocalFrameChatInputTests {
    sealed class Rig {
        public FakeDaemonClientService Daemon { get; } = new();
        public ScriptedLocalControlOps Ops { get; } = new();
        public BehaviorSubject<AgentPresence> Presence { get; } = new(new AgentPresence(null, false));
        public LocalFrameChatInput Input { get; }
        public Rig() => Input = new LocalFrameChatInput("a1", Daemon, Ops, Presence);
        public void Connected(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));
        public void Running() => Presence.OnNext(new AgentPresence(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" }, false));
    }

    [Test]
    public async Task Availability_matrix() {
        var rig = new Rig();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Connecting);
        rig.Connected("status/1");
        rig.Running();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Unsupported);
        await Assert.That(rig.Input.Hint).IsEqualTo("Update the daemon to send messages from the app");
        await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);

        rig.Connected("status/1", "input/1");
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        await Assert.That(rig.Input.CanAcceptText).IsTrue();
        await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

        rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
        await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
    }

    [Test]
    public async Task One_send_in_flight_until_the_ack_and_a_delivered_ack_clears() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        var gate = rig.Ops.ArmSendText();
        var pending = rig.Input.SendAsync("hello", CancellationToken.None);
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Sending);
        await Assert.That(rig.Input.CanAcceptText).IsFalse();
        await Assert.That(await rig.Input.SendAsync("second", CancellationToken.None)).IsFalse();
        gate.SetResult(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
        await Assert.That(await pending).IsTrue();
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        await Assert.That(rig.Ops.SendTextPayloads).IsEquivalentTo(new[] { ("a1", "hello") });
    }

    [Test]
    [Arguments("not_running", null, "agent is no longer running")]
    [Arguments("protected_kind", null, "read-only participant")]
    [Arguments("queue_full", null, "the agent's input queue is full, try again shortly")]
    [Arguments("delivery_failed", "pipe closed", "pipe closed")]
    [Arguments("transport", "eof", "delivery unconfirmed — check the chat before sending again")]
    public async Task Refusal_keeps_the_text_and_words_the_hint(string reason, string? error, string hint) {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(false, reason, error, null));
        await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo(hint);
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        rig.Ops.ArmSendText();
        _ = rig.Input.SendAsync("again", CancellationToken.None);
        await Assert.That(rig.Input.Hint).IsEqualTo("Sending…"); // the notice clears when the next send starts
    }

    [Test]
    public async Task Stopped_outcome_clears_and_unknown_ok_outcome_counts_as_delivered() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(true, null, null, SendTextOutcomes.Stopped));
        await Assert.That(await rig.Input.SendAsync("/quit", CancellationToken.None)).IsTrue();
        rig.Ops.QueueSendText(new SendTextResult(true, null, null, "future_outcome"));
        await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Cancelled_wait_keeps_the_text_with_the_unconfirmed_hint() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.ArmSendText();
        using var cts = new CancellationTokenSource();
        var pending = rig.Input.SendAsync("hello", cts.Token);
        cts.Cancel();
        await Assert.That(await pending).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
    }

    [Test]
    public async Task Session_ending_after_a_refusal_clears_the_notice() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(false, "queue_full", null, null));
        await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");

        rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
        await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
    }

    [Test]
    public async Task A_status_reemission_that_does_not_change_availability_keeps_the_notice() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        rig.Ops.QueueSendText(new SendTextResult(false, "queue_full", null, null));
        await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsFalse();
        await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");

        rig.Connected("input/1");
        await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");
    }

    [Test]
    public async Task Completion_after_dispose_mutates_nothing_and_dispose_detaches_subscriptions() {
        var rig = new Rig(); rig.Connected("input/1"); rig.Running();
        var gate = rig.Ops.ArmSendText();
        var pending = rig.Input.SendAsync("hello", CancellationToken.None);
        rig.Input.Dispose();
        var raised = 0;
        rig.Input.PropertyChanged += (_, _) => raised++;
        gate.SetResult(new SendTextResult(false, "queue_full", null, null));
        await Assert.That(await pending).IsFalse();
        rig.Connected("input/1"); rig.Running();
        rig.Presence.OnNext(new AgentPresence(null, true));
        await Assert.That(raised).IsEqualTo(0);
    }
}
