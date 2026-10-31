using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.WorkItems;
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

        public Harness(bool hasTerminal = false, IObservable<ViewerIdentityDto?>? senderIdentity = null) =>
            Input = new ServerChatInput("a1", Lane, Access, Session, hasTerminal, senderIdentity);

        public static ChatSessionInfo Info(string status, bool ended = false) =>
            new(status, status, "gemini", null, null, null, ended, "", "s1");

        public void Ready(string? subject = "u1") {
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: 1));
            Access.OnNext(SessionAccessState.Established);
        }
    }

    [Test]
    [Arguments("issuer-subject")]
    [Arguments(null)]
    public Task The_sender_uses_the_viewer_identity_instead_of_the_token_subject(string? subject) => RunOnUiAsync(async () => {
        string[] aliases = ["github:42"];
        using var identity = new BehaviorSubject<ViewerIdentityDto?>(new() { UserId = "canonical-user", Ids = aliases });
        var h = new Harness(senderIdentity: identity);
        try {
            h.Ready(subject);
            var captured = h.Input.SenderIdentity;
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.UserId).IsEqualTo("canonical-user");
            aliases[0] = "github:99";
            await Assert.That(captured.Owns("42")).IsTrue();
            await Assert.That(captured.Owns("github:99")).IsFalse();
            await Assert.That(await h.Input.SendAsync("hello", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "hello"));
        } finally { h.Input.Dispose(); }
    });

    [Test]
    public Task An_unknown_viewer_does_not_guess_a_sender_or_disable_messaging() => RunOnUiAsync(async () => {
        using var identity = new BehaviorSubject<ViewerIdentityDto?>(null);
        var h = new Harness(senderIdentity: identity);
        try {
            h.Ready("issuer-subject");
            await Assert.That(h.Input.SenderIdentity).IsNull();
            await Assert.That(h.Input.SenderUserId).IsNull();
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            await Assert.That(await h.Input.SendAsync("hello", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "hello"));
        } finally { h.Input.Dispose(); }
    });

    [Test]
    public Task The_sender_forgets_an_identity_across_account_changes_and_reconnects() => RunOnUiAsync(async () => {
        using var identity = new BehaviorSubject<ViewerIdentityDto?>(new() { UserId = "first" });
        var h = new Harness(senderIdentity: identity);
        try {
            h.Ready("issuer-subject");
            await Assert.That(h.Input.SenderIdentity!.UserId).IsEqualTo("first");
            identity.OnNext(null);
            await Assert.That(h.Input.SenderIdentity).IsNull();
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            identity.OnNext(new() { UserId = "second" });
            await Assert.That(h.Input.SenderIdentity!.UserId).IsEqualTo("second");
            h.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, Epoch: 1));
            await Assert.That(h.Input.SenderIdentity).IsNull();
            identity.OnNext(new() { UserId = "first" });
            h.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "issuer-subject", Epoch: 2));
            await Assert.That(h.Input.SenderIdentity).IsNull();
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            identity.OnNext(new() { UserId = "second" });
            await Assert.That(h.Input.SenderIdentity!.UserId).IsEqualTo("second");
        } finally { h.Input.Dispose(); }
    });

    [Test]
    public async Task An_idle_remote_session_can_accept_and_send_input() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            h.Session.OnNext(Harness.Info("Idle"));
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            await Assert.That(await h.Input.SendAsync("continue", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "continue"));
            h.Input.Dispose();
        });
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
            await Assert.That(h.Input.Hint).IsEqualTo(ServerChatInput.EndedHint);
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_the_hub_takes_is_accepted_and_a_refused_lane_leaves_it_unconfirmed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(await h.Input.SendAsync("fix it", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "fix it"));
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            await Assert.That(await h.Input.SendAsync("again", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(h.Input.Hint).Contains("delivery unconfirmed");
            h.Input.ConfirmLastSend();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            await Assert.That(await h.Input.SendAsync("nope", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Input.Hint).IsEqualTo("you cannot message this session");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_with_attachments_is_refused_before_the_hub_is_called() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(h.Input.CanAttach).IsFalse();
            await Assert.That(await h.Input.SendAsync("see this", ["0123456789abcdef0123456789abcdef"], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Lane.UserInputs).IsEmpty();
            await Assert.That(h.Input.Hint).IsEqualTo(h.Input.AttachHint);
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ready);
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task Cancellation_before_send_does_not_attempt_delivery() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(await h.Input.SendAsync("hello", [], new CancellationToken(true))).IsEqualTo(ChatSendOutcome.Rejected);
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
            await Assert.That(await h.Input.SendAsync("hello", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
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
    [Arguments(HubCallResult.Ok, "Enter sends · Shift+Enter for a new line")]
    [Arguments(HubCallResult.Denied, "you cannot interrupt this session")]
    [Arguments(HubCallResult.NotConnected, "interrupt delivery unconfirmed — check the agent's terminal")]
    public async Task Interrupt_reports_a_refused_or_unknown_outcome_and_never_claims_the_agent_stopped(HubCallResult result, string hint) {
        await RunOnUiAsync(async () => {
            var h = new Harness(hasTerminal: true);
            h.Ready();
            h.Lane.SpecialKeyHandler = _ => Task.FromResult(new HubCallOutcome(result));
            await h.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(h.Input.Hint).IsEqualTo(hint);
            h.Input.Dispose();
        });
    }

    /// The interrupt's reply is older news than a send that settled or a session that ended while
    /// it was on the wire.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_late_interrupt_reply_leaves_a_newer_hint_alone(bool sessionEnds) {
        await RunOnUiAsync(async () => {
            var h = new Harness(hasTerminal: true);
            h.Ready();
            var reply = new TaskCompletionSource<HubCallOutcome>();
            h.Lane.SpecialKeyHandler = _ => reply.Task;
            var interrupt = h.Input.InterruptAsync(CancellationToken.None);

            string expected;
            if (sessionEnds) {
                h.Session.OnNext(Harness.Info("Completed", ended: true));
                expected = ServerChatInput.EndedHint;
            } else {
                h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
                await Assert.That(await h.Input.SendAsync("continue", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
                expected = "delivery unconfirmed — check the chat before sending again";
            }
            await Assert.That(h.Input.Hint).IsEqualTo(expected);

            reply.SetResult(HubCallOutcome.NotConnected);
            await interrupt;
            await Assert.That(h.Input.Hint).IsEqualTo(expected);
            h.Input.Dispose();
        });
    }

    /// A success says nothing about the agent, so it clears a stale interrupt failure and leaves
    /// the send's own warning standing.
    [Test]
    public async Task A_successful_interrupt_clears_only_an_interrupt_hint() {
        await RunOnUiAsync(async () => {
            var h = new Harness(hasTerminal: true);
            h.Ready();
            h.Lane.SpecialKeyHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            await h.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(h.Input.Hint).IsEqualTo("interrupt delivery unconfirmed — check the agent's terminal");
            h.Lane.SpecialKeyHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            await h.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            await h.Input.SendAsync("continue", [], CancellationToken.None);
            await h.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(h.Input.Hint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_while_not_ready_is_rejected_before_it_reaches_the_lane() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(await h.Input.SendAsync("early", [], CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Lane.UserInputs).IsEmpty();
            h.Input.Dispose();
        });
    }
}
