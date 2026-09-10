using System.Reactive.Disposables;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session with no PTY: one SendText exchange per send, acked when the
/// daemon's delivery settles. A lost ack is an unknown outcome and the hint says so.
internal sealed class LocalFrameChatInput : ChatInput {
    const string InputCapability = "input/1";
    const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";

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
        _subscriptions.Add(daemon.Status.Subscribe(s => { _status = s; _notice = null; Raise(); }));
        _subscriptions.Add(presence.Subscribe(p => { _presence = p; Raise(); }));
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

    public override async Task<bool> SendAsync(string text, CancellationToken ct) {
        if (!CanAcceptText) return false;
        _sending = true; _notice = null; Raise();
        SendTextResult result;
        try {
            result = await _ops.SendTextAsync(_agentId, text, ct);
        } catch (OperationCanceledException) {
            return Settle(false, Unconfirmed);
        } catch (Exception ex) {
            return Settle(false, ex.Message);
        }
        if (result.Ok) return Settle(true, null);
        return Settle(false, result.Reason switch {
            SendTextReasons.Transport      => Unconfirmed,
            SendTextReasons.NotRunning     => "agent is no longer running",
            SendTextReasons.NoSuchAgent    => "agent is no longer running",
            SendTextReasons.ProtectedKind  => "read-only participant",
            SendTextReasons.QueueFull      => "the agent's input queue is full, try again shortly",
            SendTextReasons.StopFailed     => "the agent did not stop",
            SendTextReasons.DeliveryFailed => result.Error ?? "delivery failed",
            _                              => result.Error ?? result.Reason ?? "delivery failed",
        });
    }

    bool Settle(bool committed, string? notice) {
        if (_disposed) return committed;
        _sending = false; _notice = notice; Raise();
        return committed;
    }

    void Raise() {
        if (_disposed) return;
        this.RaisePropertyChanged(nameof(Availability));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(Hint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
