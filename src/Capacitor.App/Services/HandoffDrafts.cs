using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services;

/// Hand-off drafts awaiting their launch's verdict, for the app's lifetime: the composer that sent
/// one is gone once the new session opens, and a failure must hand the draft back to the source
/// session's next composer. Failures are buffered from construction, because one can arrive before
/// the launch call returns and the hold that would match it is made.
public sealed class HandoffDrafts : IDisposable {
    static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    static readonly TimeSpan RecentFailureTtl = TimeSpan.FromSeconds(30);

    sealed record Held(string SourceSessionId, HandoffDraft Draft, DateTimeOffset At, Action? OnFailed);

    readonly Lock _lock = new();
    readonly Dictionary<string, Held> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Reason, DateTimeOffset At)> _recentFailures = new(StringComparer.Ordinal);
    readonly Dictionary<string, HandoffDraft> _failed = new(StringComparer.Ordinal);
    readonly IAgentDirectory? _directory;
    readonly TimeProvider _time;
    readonly CompositeDisposable _subscriptions = new();
    readonly Subject<string> _failedFor = new();

    public HandoffDrafts(IObservable<LaunchFailure> failures, IAgentDirectory? directory, TimeProvider time) {
        _directory = directory;
        _time = time;
        // Settled on the lane's thread, so a hold made before the UI hop still sees the failure.
        failures
            .Select(Settle)
            .Where(onFailed => onFailed is not null)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(onFailed => onFailed!())
            .DisposeWith(_subscriptions);
        directory?.Rows.Connect()
            .Subscribe(changes => {
                foreach (var change in changes)
                    if (change.Reason is ChangeReason.Add or ChangeReason.Update
                     && change.Current is { IsPlaceholder: false, Origin: AgentOrigin.Local } row)
                        Confirm(row.Id);
            })
            .DisposeWith(_subscriptions);
    }

    /// onFailed runs on the UI thread when a failure for the agent arrives after this hold.
    public HandoffHold Hold(string sourceSessionId, string agentId, HandoffDraft draft, Action? onFailed = null) {
        var id = Key(agentId);
        if (RowExists(id)) return HandoffHold.AlreadyStarted;
        lock (_lock) {
            var now = _time.GetUtcNow();
            if (_recentFailures.Remove(id, out var failure) && now - failure.At <= RecentFailureTtl) {
                _failed[sourceSessionId] = draft with { Error = HandoffFailureText.For(failure.Reason) };
                return HandoffHold.AlreadyFailed;
            }
            _pending[id] = new Held(sourceSessionId, draft, now, onFailed);
        }
        // The row can land between the first check and the registration.
        if (!RowExists(id)) return HandoffHold.Pending;
        Confirm(id);
        return HandoffHold.AlreadyStarted;
    }

    /// The source session of a draft a later failure handed back, on the UI thread, after the
    /// hold's onFailed. Whichever composer calls TakeFailed first gets the draft.
    public IObservable<string> FailedFor => _failedFor;

    /// A draft whose composer is gone before its launch settled, for the source's next composer.
    public void HoldFailed(string sourceSessionId, HandoffDraft draft) {
        lock (_lock) _failed[sourceSessionId] = draft;
    }

    public HandoffDraft? TakeFailed(string sourceSessionId) {
        lock (_lock) return _failed.Remove(sourceSessionId, out var draft) ? draft : null;
    }

    public void Confirm(string agentId) {
        var id = Key(agentId);
        lock (_lock) {
            _pending.Remove(id);
            _recentFailures.Remove(id);
        }
    }

    Action? Settle(LaunchFailure failure) {
        var id = Key(failure.AgentId);
        lock (_lock) {
            var now = _time.GetUtcNow();
            if (_pending.Remove(id, out var held) && now - held.At <= PendingTtl) {
                _failed[held.SourceSessionId] = held.Draft with { Error = HandoffFailureText.For(failure.Reason) };
                return () => {
                    _directory?.RemovePlaceholder(id);
                    held.OnFailed?.Invoke();
                    _failedFor.OnNext(held.SourceSessionId);
                };
            }
            _recentFailures[id] = (failure.Reason, now);
            foreach (var stale in _recentFailures.Where(kv => now - kv.Value.At > RecentFailureTtl).Select(kv => kv.Key).ToList())
                _recentFailures.Remove(stale);
            foreach (var stale in _pending.Where(kv => now - kv.Value.At > PendingTtl).Select(kv => kv.Key).ToList())
                _pending.Remove(stale);
            return null;
        }
    }

    bool RowExists(string id) =>
        _directory?.Rows.Items.Any(r => r is { IsPlaceholder: false, Origin: AgentOrigin.Local } && Key(r.Id) == id) == true;

    static string Key(string agentId) => AgentIds.Normalize(agentId) ?? agentId;

    public void Dispose() {
        _subscriptions.Dispose();
        _failedFor.Dispose();
    }
}
