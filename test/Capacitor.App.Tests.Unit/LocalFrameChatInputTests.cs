using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// The daemon-status stream is marshalled onto the main thread, so every test runs under the
/// session's immediate main-thread scheduler: an emission then applies before the next line, and
/// the process-global scheduler is what the class constraint protects.
/// </summary>
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
    [NotInParallel("AvaloniaSession")]
    public async Task Availability_matrix() {
        await RunOnUiAsync(async () => {
            var rig = new Rig();
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            rig.Connected("status/1");
            rig.Running();
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Unsupported);
            await Assert.That(rig.Input.Hint).IsEqualTo("Update the daemon to send messages from the app");
            await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);

            rig.Connected("status/1", "input/1");
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(rig.Input.CanAcceptText).IsTrue();
            await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task One_send_in_flight_until_the_ack_and_a_delivered_ack_clears() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            var gate = rig.Ops.ArmSendText();
            var pending = rig.Input.SendAsync("hello", CancellationToken.None);
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Sending);
            await Assert.That(rig.Input.CanAcceptText).IsFalse();
            await Assert.That(await rig.Input.SendAsync("second", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            gate.SetResult(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
            await Assert.That(await pending).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(rig.Ops.SendTextPayloads).IsEquivalentTo(new[] { ("a1", "hello") });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("not_running", null, "agent is no longer running")]
    [Arguments("protected_kind", null, "read-only participant")]
    [Arguments("queue_full", null, "the agent's input queue is full, try again shortly")]
    [Arguments("reaper_claimed", null, "the agent is being stopped")]
    [Arguments("reaper_claimed_late", null, "the agent is being stopped")]
    [Arguments("delivery_failed", "pipe closed", "pipe closed")]
    [Arguments("too_large", "text exceeds 262144 bytes", "text exceeds 262144 bytes")]
    [Arguments("transport", "eof", "delivery unconfirmed — check the chat before sending again")]
    // A reason this build has no wording for still never shows the caller the wire token.
    [Arguments("some_future_reason", "raw detail", "delivery failed")]
    public async Task Refusal_keeps_the_text_and_words_the_hint(string reason, string? error, string hint) {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Ops.QueueSendText(new SendTextResult(false, reason, error, null));
            await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsEqualTo(
                reason == SendTextReasons.Transport ? ChatSendOutcome.Unconfirmed : ChatSendOutcome.Rejected);
            await Assert.That(rig.Input.Hint).IsEqualTo(hint);
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            rig.Ops.ArmSendText();
            _ = rig.Input.SendAsync("again", CancellationToken.None);
            await Assert.That(rig.Input.Hint).IsEqualTo("Sending…"); // the notice clears when the next send starts
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stopped_outcome_clears_and_unknown_ok_outcome_counts_as_delivered() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Ops.QueueSendText(new SendTextResult(true, null, null, SendTextOutcomes.Stopped));
            await Assert.That(await rig.Input.SendAsync("/quit", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            rig.Ops.QueueSendText(new SendTextResult(true, null, null, "future_outcome"));
            await Assert.That(await rig.Input.SendAsync("x", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelled_wait_keeps_the_text_with_the_unconfirmed_hint() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Ops.ArmSendText();
            using var cts = new CancellationTokenSource();
            var pending = rig.Input.SendAsync("hello", cts.Token);
            await cts.CancelAsync();
            await Assert.That(await pending).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Session_ending_after_a_refusal_clears_the_notice() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Ops.QueueSendText(new SendTextResult(false, "queue_full", null, null));
            await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");

            rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancellation_before_send_does_not_attempt_delivery() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            await Assert.That(await rig.Input.SendAsync("hello", new CancellationToken(true))).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);
            rig.Input.Dispose();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_transport_exception_is_unconfirmed_until_delivery_is_confirmed() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            var gate = rig.Ops.ArmSendText();
            var send = rig.Input.SendAsync("hello", CancellationToken.None);
            gate.SetException(new IOException("connection closed"));
            await Assert.That(await send).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
            rig.Input.ConfirmLastSend();
            await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            rig.Input.Dispose();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_status_reemission_that_does_not_change_availability_keeps_the_notice() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Ops.QueueSendText(new SendTextResult(false, "queue_full", null, null));
            await Assert.That(await rig.Input.SendAsync("hello", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");

            rig.Connected("input/1");
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completion_after_dispose_mutates_nothing_and_dispose_detaches_subscriptions() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            var gate = rig.Ops.ArmSendText();
            var pending = rig.Input.SendAsync("hello", CancellationToken.None);
            rig.Input.Dispose();
            var raised = 0;
            rig.Input.PropertyChanged += (_, _) => raised++;
            gate.SetResult(new SendTextResult(false, "queue_full", null, null));
            await Assert.That(await pending).IsEqualTo(ChatSendOutcome.Rejected);
            rig.Connected("input/1"); rig.Running();
            rig.Presence.OnNext(new AgentPresence(null, true));
            await Assert.That(raised).IsEqualTo(0);
        });
    }

    /// Availability still reads Ready after a dispose (it is derived from the last status and dto),
    /// so the entry guard is the only thing keeping a torn-down tab from sending.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_disposed_channel_sends_nothing_even_while_it_reads_ready() {
        await RunOnUiAsync(async () => {
            var rig = new Rig(); rig.Connected("input/1"); rig.Running();
            rig.Input.Dispose();

            await Assert.That(rig.Input.CanAcceptText).IsTrue();
            await Assert.That(await rig.Input.SendAsync("after", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);
        });
    }
}
