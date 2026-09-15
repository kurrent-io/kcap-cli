using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class TerminalChatInputTests {
    const string Id = "0123456789abcdef0123456789abcdef";

    sealed class Rig {
        public required TerminalTabViewModel Terminal { get; init; }
        public required TerminalChatInput Input { get; init; }
        public required FakeTerminalAttachClient Client { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required FakeDaemonClientService Daemon { get; init; }
        public required ScriptedLocalControlOps Ops { get; init; }
        public required BehaviorSubject<AgentPresence> Presence { get; init; }

        public void Connected(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));
        public void RunningAt(string? workLocation) =>
            Presence.OnNext(new AgentPresence(Agent("a1", "claude", hasTerminal: true, workLocation: workLocation) with { Status = "Running" }, false));
    }

    static async Task<Rig> BuildAttachedAsync() {
        var daemon = new FakeDaemonClientService();
        var factory = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var ops = new ScriptedLocalControlOps();
        var presence = new BehaviorSubject<AgentPresence>(new AgentPresence(null, false));
        var terminal = new TerminalTabViewModel("a1", daemon, factory.Factory, () => new FakeTerminalSurface(), time);
        var input = new TerminalChatInput(terminal, "a1", daemon, ops, presence);
        daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
        Dispatcher.UIThread.RunJobs();
        await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        var client = factory.Created.Single();
        await client.TriggerAttached([]);
        return new Rig {
            Terminal = terminal, Input = input, Client = client, Time = time,
            Daemon = daemon, Ops = ops, Presence = presence,
        };
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Mirrors_the_terminal_availability_and_wording() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            await Assert.That(rig.Input.CanAcceptText).IsTrue();

            var raised = new List<string>();
            rig.Input.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
            await Assert.That(await rig.Input.SendAsync("hello", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Sending);
            await Assert.That(rig.Input.Hint).IsEqualTo("Sending…");
            await Assert.That(raised).Contains(nameof(ChatInput.Availability));

            rig.Time.Advance(TimeSpan.FromMilliseconds(150));
            await rig.Terminal.PendingDeliveryForTesting!;
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(2);
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_detaches_from_the_terminal() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Input.Dispose();
            var raised = 0;
            rig.Input.PropertyChanged += (_, _) => raised++;
            rig.Client.Result.SetResult(new AttachOutcome.Exited(0));
            await rig.Terminal.CurrentRunForTesting!;
            await Assert.That(raised).IsEqualTo(0);
            await Assert.That(await rig.Input.SendAsync("x", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_waits_for_submit_and_does_not_paste_or_submit_an_extra_message() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            await rig.Input.SendAsync("follow-up", [], CancellationToken.None);
            await Assert.That(rig.Input.CanInterrupt).IsTrue();
            var interrupt = rig.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(1);
            rig.Time.Advance(TimeSpan.FromMilliseconds(150));
            await interrupt;
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(3);
            await Assert.That(rig.Client.SentInput[1]).IsEquivalentTo(new byte[] { 0x0d });
            await Assert.That(rig.Client.SentInput[2]).IsEquivalentTo(new byte[] { 0x1b });
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detaching_during_submit_prevents_the_waiting_escape_from_reaching_the_old_client() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            await rig.Input.SendAsync("follow-up", [], CancellationToken.None);
            var interrupt = rig.Input.InterruptAsync(CancellationToken.None);
            rig.Client.Result.SetResult(new AttachOutcome.Detached());
            await rig.Terminal.CurrentRunForTesting!;
            rig.Time.Advance(TimeSpan.FromMilliseconds(150));
            await interrupt;
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(1);
            await Assert.That(rig.Input.CanInterrupt).IsFalse();
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    /// Text alone never leaves the PTY, and a prompt carrying ids never writes to it — the frame
    /// exchange delivers both halves, so a submit from the app would duplicate the message.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_only_stays_on_the_terminal_and_ids_take_one_frame_exchange_closing_the_gate_meanwhile() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Connected("status/1", "input/1", "input/2");
            rig.RunningAt(WorkLocationText.Owned);

            await Assert.That(await rig.Input.SendAsync("hi", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(rig.Ops.SendTextWithAttachmentsCalls).IsEqualTo(0);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(1);
            rig.Time.Advance(TimeSpan.FromMilliseconds(150));
            await rig.Terminal.PendingDeliveryForTesting!;
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(2);

            var gate = rig.Ops.ArmSendText();
            var send = rig.Input.SendAsync("hi", [Id], CancellationToken.None);
            await Assert.That(rig.Input.CanAcceptText).IsFalse();
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.Hint).IsEqualTo("Sending…");
            gate.SetResult(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
            await Assert.That(await send).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(rig.Input.CanAcceptText).IsTrue();
            await Assert.That(rig.Ops.SendTextWithAttachmentsPayloads.Single().Ids).IsEquivalentTo(new[] { Id });
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(2);
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Can_attach_follows_input_2_and_work_location() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.RunningAt(WorkLocationText.Owned);
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.AttachHint).IsNull(); // no capability list yet: nothing to blame

            rig.Connected("status/1", "input/1");
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.AttachHint).IsEqualTo("attachments need the daemon updated");

            rig.Connected("status/1", "input/1", "input/2");
            rig.RunningAt(WorkLocationText.Borrowed);
            await Assert.That(rig.Input.CanAttach).IsFalse();
            await Assert.That(rig.Input.AttachHint).IsEqualTo("attachments aren't available for an in-place session");

            rig.RunningAt(WorkLocationText.Owned);
            await Assert.That(rig.Input.CanAttach).IsTrue();
            await Assert.That(rig.Input.AttachHint).IsNull();
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    /// The gate refuses before the wire and before the PTY: neither half of the prompt moves.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_shut_gate_refuses_ids_without_sending_anything() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Connected("status/1", "input/1");
            rig.RunningAt(WorkLocationText.Owned);
            await Assert.That(rig.Input.CanAcceptText).IsTrue();
            await Assert.That(await rig.Input.SendAsync("hi", [Id], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextWithAttachmentsCalls).IsEqualTo(0);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(0);

            rig.Connected("status/1", "input/1", "input/2");
            rig.RunningAt(WorkLocationText.Borrowed);
            await Assert.That(await rig.Input.SendAsync("hi", [Id], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Ops.SendTextWithAttachmentsCalls).IsEqualTo(0);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(0);
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_text_send_while_an_attachment_send_is_in_flight_is_refused_and_never_pasted() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Connected("input/1", "input/2");
            rig.RunningAt(WorkLocationText.Owned);
            var gate = rig.Ops.ArmSendText();
            var send = rig.Input.SendAsync("hi", [Id], CancellationToken.None);

            await Assert.That(await rig.Input.SendAsync("typed", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Client.SentInput).Count().IsEqualTo(0);

            gate.SetResult(new SendTextResult(true, null, null, SendTextOutcomes.Delivered));
            await Assert.That(await send).IsEqualTo(ChatSendOutcome.Accepted);
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lost_ack_is_unconfirmed_and_only_that_notice_is_cleared_by_a_receipt() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Connected("input/1", "input/2");
            rig.RunningAt(WorkLocationText.Owned);

            rig.Ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, "eof", null));
            await Assert.That(await rig.Input.SendAsync("hi", [Id], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
            rig.Input.ConfirmLastSend();
            await Assert.That(rig.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            var gate = rig.Ops.ArmSendText();
            var send = rig.Input.SendAsync("hi", [Id], CancellationToken.None);
            gate.SetException(new IOException("connection closed"));
            await Assert.That(await send).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(rig.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");

            rig.Ops.QueueSendText(new SendTextResult(false, SendTextReasons.QueueFull, null, null));
            await Assert.That(await rig.Input.SendAsync("hi", [Id], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            rig.Input.ConfirmLastSend();
            await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Session_ending_after_a_refusal_clears_the_notice() {
        await RunOnUiAsync(async () => {
            var rig = await BuildAttachedAsync();
            rig.Connected("input/1", "input/2");
            rig.RunningAt(WorkLocationText.Owned);
            rig.Ops.QueueSendText(new SendTextResult(false, SendTextReasons.QueueFull, null, null));
            await Assert.That(await rig.Input.SendAsync("hi", [Id], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(rig.Input.Hint).IsEqualTo("the agent's input queue is full, try again shortly");

            rig.Client.Result.SetResult(new AttachOutcome.Exited(0));
            await rig.Terminal.CurrentRunForTesting!;
            await Assert.That(rig.Input.Availability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(rig.Input.Hint).IsEqualTo("This session has ended");
            rig.Input.Dispose();
            await rig.Terminal.TeardownAsync();
        });
    }
}
