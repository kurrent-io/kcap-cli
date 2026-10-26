using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session on another machine: one hub call per send, accepted when
/// the hub takes it. A refused send answers nothing on the wire, so acceptance is the hub's and
/// delivery is the transcript's — the same unconfirmed-until-echoed rule as the local frame.
internal sealed class ServerChatInput : ChatInput {
    const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";
    const string InterruptUnconfirmed = "interrupt delivery unconfirmed — check the agent's terminal";
    const string InterruptDenied = "you cannot interrupt this session";

    readonly string _agentId;
    readonly IServerLane _lane;
    readonly bool _hasTerminal;
    readonly CompositeDisposable _subscriptions = new();
    ServerLaneState _laneState = ServerLaneState.Dormant;
    SessionAccessState _access = SessionAccessState.Establishing;
    ChatSessionInfo? _session;
    bool _sending;
    bool _disposed;
    ViewerIdentityDto? _senderIdentity;
    bool _hasConnected;
    string? _notice;
    // Bumped by every send and availability change, so an interrupt reply that lands after one of
    // them leaves the newer hint alone.
    int _noticeVersion;

    public ServerChatInput(string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<ChatSessionInfo> session, bool hasTerminal,
            IObservable<ViewerIdentityDto?>? senderIdentity = null) {
        _agentId = agentId;
        _lane = lane;
        _hasTerminal = hasTerminal;
        _subscriptions.Add(lane.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Apply(() => {
            _laneState = s.State;
            if (s.State == ServerLaneState.Connected) _hasConnected = true;
            else _senderIdentity = null;
        })));
        _subscriptions.Add(access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(a => Apply(() => _access = a)));
        _subscriptions.Add(session.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(i => Apply(() => _session = i)));
        // The token subject belongs to its issuer; the identity endpoint names the server's actor and aliases.
        if (senderIdentity is not null)
            _subscriptions.Add(senderIdentity.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(identity => Apply(() => {
                _senderIdentity = (!_hasConnected || _laneState == ServerLaneState.Connected)
                    && identity is { UserId.Length: > 0 }
                        ? identity with { Ids = Array.AsReadOnly(identity.Ids.ToArray()) }
                        : null;
            })));
    }

    void Apply(Action update) {
        var before = Availability;
        update();
        if (Availability != before) {
            _notice = null;
            _noticeVersion++;
        }
        Raise();
    }

    public override SendAvailability Availability =>
        _disposed || _session?.Ended == true ? SendAvailability.Ended
        : _laneState != ServerLaneState.Connected || _access != SessionAccessState.Established ? SendAvailability.Connecting
        : _sending ? SendAvailability.Sending
        : _session?.Status is "Running" or "Idle" ? SendAvailability.Ready
        : SendAvailability.Connecting;

    public override bool CanAcceptText => Availability == SendAvailability.Ready;
    public override ViewerIdentityDto? SenderIdentity => !_disposed && _laneState == ServerLaneState.Connected
        && _access == SessionAccessState.Established ? _senderIdentity : null;
    public override string? SenderUserId => SenderIdentity?.UserId;
    public override bool CanDiscardQueued => !_disposed && _laneState == ServerLaneState.Connected && _access == SessionAccessState.Established;

    public override async Task<bool> DiscardQueuedAsync(Guid dispatchId, CancellationToken ct) {
        if (!CanDiscardQueued || ct.IsCancellationRequested) return false;
        try {
            var outcome = await _lane.DiscardQueuedInputAsync(_agentId, dispatchId, ct);
            if (outcome.Result == HubCallResult.Ok) return true;
            Console.Error.WriteLine($"kcap: discard {dispatchId} failed: {outcome.Result}: {outcome.Reason}");
            return false;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) {
            Console.Error.WriteLine($"kcap: discard {dispatchId} failed: {ex.Message}");
            return false;
        }
    }

    /// Escape is a PTY keystroke; a frame-driven harness has nothing it would reach.
    public override bool CanInterrupt => _hasTerminal && CanAcceptText;

    public override string Hint => _notice ?? Availability switch {
        SendAvailability.Ready   => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending => "Sending…",
        SendAvailability.Ended   => "This session has ended",
        _                        => "Connecting to the session…",
    };

    /// A session on another machine has no attachment lane yet: the hub call carries text alone.
    public override bool CanAttach => false;
    public override string? AttachHint => "attachments to a session on another machine are not supported yet";

    public override async Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        if (_disposed || !CanAcceptText || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        if (attachmentIds.Count > 0) return Settle(ChatSendOutcome.Rejected, AttachHint);
        _sending = true; _notice = null; _noticeVersion++; Raise();
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SendUserInputAsync(_agentId, text, ct);
        } catch (Exception) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        }
        return outcome.Result switch {
            HubCallResult.Ok     => Settle(ChatSendOutcome.Accepted, null),
            HubCallResult.Denied => Settle(ChatSendOutcome.Rejected, "you cannot message this session"),
            _                    => Settle(ChatSendOutcome.Unconfirmed, Unconfirmed),
        };
    }

    /// The hub taking the key proves nothing about the agent stopping, so only a refusal or an
    /// unknown outcome is worth a hint; a later success clears just that hint.
    public override async Task InterruptAsync(CancellationToken ct) {
        if (!CanInterrupt || ct.IsCancellationRequested) return;
        var version = _noticeVersion;
        string? notice;
        try {
            notice = (await _lane.SendSpecialKeyAsync(_agentId, SpecialKeys.Escape, ct)).Result switch {
                HubCallResult.Ok     => null,
                HubCallResult.Denied => InterruptDenied,
                _                    => InterruptUnconfirmed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) {
            Console.Error.WriteLine($"kcap: composer interrupt failed: {ex.Message}");
            notice = InterruptUnconfirmed;
        }
        if (_disposed || version != _noticeVersion) return;
        if (notice is null && _notice is not (InterruptUnconfirmed or InterruptDenied)) return;
        _notice = notice;
        Raise();
    }

    ChatSendOutcome Settle(ChatSendOutcome outcome, string? notice) {
        if (_disposed) return outcome;
        _sending = false; _notice = notice; _noticeVersion++; Raise();
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
        this.RaisePropertyChanged(nameof(CanDiscardQueued));
        this.RaisePropertyChanged(nameof(SenderIdentity));
        this.RaisePropertyChanged(nameof(SenderUserId));
        this.RaisePropertyChanged(nameof(Hint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
