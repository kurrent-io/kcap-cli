using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The hub composer channel: ready only with a live lane, an established session and a running
/// agent; accepted when the hub takes the send; the transcript's echo is what confirms delivery.
[NotInParallel(nameof(AvaloniaSession))]
public class ServerChatInputTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<ChatSessionInfo> Session = new(Info("Running"));
        public readonly ServerChatInput Input;

        public Harness(bool hasTerminal = false) => Input = new ServerChatInput("a1", Lane, Access, Session, hasTerminal);

        public static ChatSessionInfo Info(string status, bool ended = false) =>
            new(status, status, "gemini", null, null, null, ended, "", "s1");

        public void Ready() {
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            Access.OnNext(SessionAccessState.Established);
        }
    }

    [Test]
    public async Task Ready_needs_a_live_lane_an_established_session_and_a_running_agent() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Access.OnNext(SessionAccessState.Established);
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Session.OnNext(Harness.Info("Starting"));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Session.OnNext(Harness.Info("Completed", ended: true));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(h.Input.Hint).IsEqualTo("This session has ended");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_the_hub_takes_is_accepted_and_a_refused_lane_leaves_it_unconfirmed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(await h.Input.SendAsync("fix it", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "fix it"));
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            await Assert.That(await h.Input.SendAsync("again", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(h.Input.Hint).Contains("delivery unconfirmed");
            h.Input.ConfirmLastSend();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            await Assert.That(await h.Input.SendAsync("nope", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Input.Hint).IsEqualTo("you cannot message this session");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task Cancellation_before_send_does_not_attempt_delivery() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(await h.Input.SendAsync("hello", new CancellationToken(true))).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Lane.UserInputs).IsEmpty();
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_transport_exception_is_unconfirmed_until_delivery_is_confirmed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            h.Lane.UserInputHandler = _ => Task.FromException<HubCallOutcome>(new IOException("socket"));
            await Assert.That(await h.Input.SendAsync("hello", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(h.Input.Hint).Contains("delivery unconfirmed");
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ready);
            h.Input.ConfirmLastSend();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task Interrupt_sends_escape_only_for_a_terminal_harness() {
        await RunOnUiAsync(async () => {
            var pty = new Harness(hasTerminal: true);
            await Assert.That(pty.Input.CanInterrupt).IsFalse();
            pty.Ready();
            await Assert.That(pty.Input.CanInterrupt).IsTrue();
            await pty.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(pty.Lane.SpecialKeys).Contains(("a1", SpecialKeys.Escape));
            pty.Input.Dispose();

            var frame = new Harness(hasTerminal: false);
            frame.Ready();
            await Assert.That(frame.Input.CanInterrupt).IsFalse();
            await frame.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(frame.Lane.SpecialKeys).IsEmpty();
            frame.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_while_not_ready_is_rejected_before_it_reaches_the_lane() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(await h.Input.SendAsync("early", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Lane.UserInputs).IsEmpty();
            h.Input.Dispose();
        });
    }
}
