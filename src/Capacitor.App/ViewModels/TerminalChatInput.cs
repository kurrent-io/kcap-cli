using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session with a PTY. Text alone is a paste the terminal commits, but a
/// prompt carrying attachments has no PTY representation: it takes the daemon's frame exchange
/// instead, and nothing is written to the terminal for it.
internal sealed class TerminalChatInput : ChatInput {
    readonly TerminalTabViewModel _terminal;
    readonly string _agentId;
    readonly ILocalControlOps _ops;
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _status = new(AttachState.Connecting, null, null);
    AgentPresence _presence = new(null, false);
    SendAvailability _terminalAvailability;
    bool _sending;
    bool _disposed;
    string? _notice;

    public TerminalChatInput(
            TerminalTabViewModel terminal, string agentId, IDaemonClientService daemon, ILocalControlOps ops,
            IObservable<AgentPresence> presence) {
        _terminal = terminal;
        _agentId = agentId;
        _ops = ops;
        _terminalAvailability = terminal.SendAvailability;
        _subscriptions.Add(terminal.WhenAnyValue(t => t.SendAvailability, t => t.State, t => t.CanAcceptText)
            .Skip(1)
            .Subscribe(_ => ApplyTerminal()));
        // Status comes off the daemon client's worker thread; presence is already marshalled by the
        // workspace that publishes it.
        _subscriptions.Add(daemon.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => { _status = s; Raise(); }));
        _subscriptions.Add(presence.Subscribe(p => { _presence = p; Raise(); }));
    }

    /// A refusal notice describes the send that earned it; a terminal that detached, reattached or
    /// ended since has made it stale, so it goes with the availability that produced it.
    void ApplyTerminal() {
        if (_terminal.SendAvailability != _terminalAvailability) {
            _terminalAvailability = _terminal.SendAvailability;
            _notice = null;
        }
        Raise();
    }

    public override SendAvailability Availability =>
        _disposed ? SendAvailability.Ended : _sending ? SendAvailability.Sending : _terminal.SendAvailability;
    public override bool CanAcceptText => !_disposed && !_sending && _terminal.CanAcceptText;
    public override bool CanInterrupt => !_disposed && _terminal.CanInterrupt;
    public override Task InterruptAsync(CancellationToken ct) =>
        CanInterrupt ? _terminal.SendEscapeAsync(ct) : Task.CompletedTask;
    public override Task<bool> SendKeyAsync(byte key, CancellationToken ct) => _terminal.SendRawAsync(key, ct);
    public override string Hint => _sending ? "Sending…" : _notice ?? HintFor(_terminal.SendAvailability, _terminal.State);

    public override bool CanAttach =>
        CanAcceptText && LocalFrameChatInput.HasAttachCapability(_status) && LocalFrameChatInput.IsOwnedWorktree(_presence);

    public override string? AttachHint =>
        CanAttach ? null : LocalFrameChatInput.AttachHintFor(_status, _presence, Hint);

    /// The terminal path has nothing to cancel: acceptance is synchronous. Attachments do not
    /// travel that path, so they wait on the daemon's ack like the frame channel's sends.
    public override async Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        if (_disposed || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        if (attachmentIds.Count == 0)
            return CanAcceptText && _terminal.TrySendText(text) ? ChatSendOutcome.Accepted : ChatSendOutcome.Rejected;
        // The capability can go while the upload runs, and a bare rejection leaves the chips sitting
        // in the composer with nothing said about them.
        if (!CanAttach)
            return Settle(ChatSendOutcome.Rejected, AttachHint ?? LocalFrameChatInput.AttachmentsRefused);
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try {
            result = await _ops.SendTextWithAttachmentsAsync(_agentId, text, attachmentIds, ct);
        } catch (Exception) {
            return Settle(ChatSendOutcome.Unconfirmed, LocalFrameChatInput.Unconfirmed);
        }
        if (result.Ok) return Settle(ChatSendOutcome.Accepted, null);
        var outcome = result.Reason == SendTextReasons.Transport ? ChatSendOutcome.Unconfirmed : ChatSendOutcome.Rejected;
        return Settle(outcome, LocalFrameChatInput.NoticeFor(result));
    }

    ChatSendOutcome Settle(ChatSendOutcome outcome, string? notice) {
        if (_disposed) return outcome;
        _sending = false; _notice = notice; Raise();
        return outcome;
    }

    public override void ConfirmLastSend() {
        if (_disposed || _sending || _notice != LocalFrameChatInput.Unconfirmed) return;
        _notice = null;
        Raise();
    }

    void Raise() {
        if (_disposed) return;
        this.RaisePropertyChanged(nameof(Availability));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(Hint));
        this.RaisePropertyChanged(nameof(CanInterrupt));
        this.RaisePropertyChanged(nameof(CanAttach));
        this.RaisePropertyChanged(nameof(AttachHint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }

    /// The hint is built from the terminal's own availability, so it is true in the windows where
    /// State alone would lie (a reattach or detach under way while State reads Attached).
    internal static string HintFor(SendAvailability availability, TerminalSessionState state) => availability switch {
        SendAvailability.Ready         => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending       => "Sending…",
        SendAvailability.Transitioning => "Updating the terminal connection…",
        SendAvailability.ReadOnly      => $"Read-only: {state.Detail}",
        SendAvailability.Connecting    => "Connecting to the terminal…",
        SendAvailability.Reattach      => "Reattach the terminal to send",
        SendAvailability.Ended         => "This session has ended",
        _                              => "No terminal to send to",
    };
}
