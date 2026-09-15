using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session on another machine: one hub call per send, accepted when
/// the hub takes it. A refused send answers nothing on the wire, so acceptance is the hub's and
/// delivery is the transcript's — the same unconfirmed-until-echoed rule as the local frame.
internal sealed class ServerChatInput : ChatInput {
    const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";

    readonly string _agentId;
    readonly IServerLane _lane;
    readonly bool _hasTerminal;
    readonly CompositeDisposable _subscriptions = new();
    ServerLaneState _laneState = ServerLaneState.Dormant;
    SessionAccessState _access = SessionAccessState.Establishing;
    ChatSessionInfo? _session;
    bool _sending;
    bool _disposed;
    string? _notice;

    public ServerChatInput(string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<ChatSessionInfo> session, bool hasTerminal) {
        _agentId = agentId;
        _lane = lane;
        _hasTerminal = hasTerminal;
        _subscriptions.Add(lane.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Apply(() => _laneState = s.State)));
        _subscriptions.Add(access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(a => Apply(() => _access = a)));
        _subscriptions.Add(session.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(i => Apply(() => _session = i)));
    }

    void Apply(Action update) {
        var before = Availability;
        update();
        if (Availability != before) _notice = null;
        Raise();
    }

    public override SendAvailability Availability =>
        _disposed || _session?.Ended == true ? SendAvailability.Ended
        : _laneState != ServerLaneState.Connected || _access != SessionAccessState.Established ? SendAvailability.Connecting
        : _sending ? SendAvailability.Sending
        : _session?.Status == "Running" ? SendAvailability.Ready
        : SendAvailability.Connecting;

    public override bool CanAcceptText => Availability == SendAvailability.Ready;

    /// Escape is a PTY keystroke; a frame-driven harness has nothing it would reach.
    public override bool CanInterrupt => _hasTerminal && CanAcceptText;

    public override string Hint => _notice ?? Availability switch {
        SendAvailability.Ready   => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending => "Sending…",
        SendAvailability.Ended   => "This session has ended",
        _                        => "Connecting to the session…",
    };

    public override async Task<ChatSendOutcome> SendAsync(string text, CancellationToken ct) {
        if (_disposed || !CanAcceptText || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        _sending = true; _notice = null; Raise();
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SendUserInputAsync(_agentId, text, ct);
        } catch (OperationCanceledException) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        } catch (Exception) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        }
        return outcome.Result switch {
            HubCallResult.Ok     => Settle(ChatSendOutcome.Accepted, null),
            HubCallResult.Denied => Settle(ChatSendOutcome.Rejected, "you cannot message this session"),
            _                    => Settle(ChatSendOutcome.Unconfirmed, Unconfirmed),
        };
    }

    public override async Task InterruptAsync(CancellationToken ct) {
        if (!CanInterrupt || ct.IsCancellationRequested) return;
        try { await _lane.SendSpecialKeyAsync(_agentId, SpecialKeys.Escape, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: composer interrupt failed: {ex.Message}"); }
    }

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
        this.RaisePropertyChanged(nameof(CanInterrupt));
        this.RaisePropertyChanged(nameof(Hint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
