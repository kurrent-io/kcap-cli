using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session with no PTY: one SendText exchange per send, acked when the
/// daemon's delivery settles. A lost ack is an unknown outcome and the hint says so.
internal sealed class LocalFrameChatInput : ChatInput {
    const string InputCapability = "input/1";
    const string AttachCapability = "input/2";
    internal const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";

    readonly string _agentId;
    readonly ILocalControlOps _ops;
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _status = new(AttachState.Connecting, null, null);
    AgentPresence _presence = new(null, false);
    bool _sending;
    bool _disposed;
    string? _notice;

    public LocalFrameChatInput(string agentId, IDaemonClientService daemon, ILocalControlOps ops, IObservable<AgentPresence> presence) {
        _agentId = agentId;
        _ops = ops;
        // Status comes off the daemon client's worker thread; presence is already marshalled by the
        // workspace that publishes it.
        _subscriptions.Add(daemon.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplyStatus));
        _subscriptions.Add(presence.Subscribe(ApplyPresence));
    }

    void ApplyStatus(AttachStatus s) {
        var before = Availability;
        _status = s;
        if (Availability != before) _notice = null;
        Raise();
    }

    void ApplyPresence(AgentPresence p) {
        var before = Availability;
        _presence = p;
        if (Availability != before) _notice = null;
        Raise();
    }

    public override SendAvailability Availability =>
        _presence.SessionEnded ? SendAvailability.Ended
        : _status.State != AttachState.Connected ? SendAvailability.Connecting
        : _status.Capabilities is not { } caps || !caps.Contains(InputCapability) ? SendAvailability.Unsupported
        : _sending ? SendAvailability.Sending
        : _presence.Dto?.Status == "Running" ? SendAvailability.Ready
        : SendAvailability.Connecting;

    public override bool CanAcceptText => Availability == SendAvailability.Ready;

    public override string Hint => _notice ?? Availability switch {
        SendAvailability.Ready       => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending     => "Sending…",
        SendAvailability.Unsupported => "Update the daemon to send messages from the app",
        SendAvailability.Ended       => "This session has ended",
        _                            => "Connecting to the agent…",
    };

    public override bool CanAttach =>
        Availability == SendAvailability.Ready && HasAttachCapability(_status) && IsOwnedWorktree(_presence);

    public override string? AttachHint => CanAttach ? null : AttachHintFor(_status, _presence, Hint);

    internal static bool HasAttachCapability(AttachStatus status) =>
        status.Capabilities is { } caps && caps.Contains(AttachCapability);

    internal static bool IsOwnedWorktree(AgentPresence presence) =>
        string.Equals(presence.Dto?.WorkLocation, WorkLocationText.Owned, StringComparison.Ordinal);

    /// Null until a capability list has arrived: a connecting daemon has not yet said whether it
    /// takes attachments, and blaming its version for that would be a guess.
    internal static string? AttachHintFor(AttachStatus status, AgentPresence presence, string fallback) =>
        status.Capabilities is null ? null
        : !HasAttachCapability(status) ? "attachments need the daemon updated"
        : !IsOwnedWorktree(presence) ? "attachments aren't available for an in-place session"
        : fallback;

    public override async Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        if (_disposed || !CanAcceptText || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        if (attachmentIds.Count > 0 && !CanAttach) return ChatSendOutcome.Rejected;
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try {
            result = attachmentIds.Count == 0
                ? await _ops.SendTextAsync(_agentId, text, ct)
                : await _ops.SendTextWithAttachmentsAsync(_agentId, text, attachmentIds, ct);
        } catch (OperationCanceledException) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        } catch (Exception) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        }
        if (result.Ok) return Settle(ChatSendOutcome.Accepted, null);
        var outcome = result.Reason == SendTextReasons.Transport ? ChatSendOutcome.Unconfirmed : ChatSendOutcome.Rejected;
        return Settle(outcome, NoticeFor(result));
    }

    internal static string NoticeFor(SendTextResult result) => result.Reason switch {
        SendTextReasons.Transport           => Unconfirmed,
        SendTextReasons.NotRunning          => "agent is no longer running",
        SendTextReasons.NoSuchAgent         => "agent is no longer running",
        SendTextReasons.ProtectedKind       => "read-only participant",
        SendTextReasons.QueueFull           => "the agent's input queue is full, try again shortly",
        SendTextReasons.StopFailed          => "the agent did not stop",
        SendTextReasons.ReaperClaimed or SendTextReasons.ReaperClaimedLate => "the agent is being stopped",
        SendTextReasons.TooLarge            => result.Error ?? "message is too large",
        SendTextReasons.DeliveryFailed      => result.Error ?? "delivery failed",
        SendTextReasons.AttachmentsRefused  => result.Error ?? "attachments were refused",
        // A reason this build has no wording for is still not composer text: the raw wire token
        // would read as a bug report to the user.
        _                                   => "delivery failed",
    };

    ChatSendOutcome Settle(ChatSendOutcome outcome, string? notice) {
        if (_disposed) return outcome;
        _sending = false; _notice = notice; Raise();
        return outcome;
    }

    public override void ConfirmLastSend() {
        if (_disposed || _sending || _notice != Unconfirmed) return;
        _notice = null;
        Raise();
    }

    void Raise() {
        if (_disposed) return;
        this.RaisePropertyChanged(nameof(Availability));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(Hint));
        this.RaisePropertyChanged(nameof(CanAttach));
        this.RaisePropertyChanged(nameof(AttachHint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
