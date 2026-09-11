using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// Feeds the permission cache's server lane: live pushes for sessions the app has joined, a
/// reconciliation of each session's event stream every time its access is established, and the
/// org-wide settlement pings. Every reconciliation carries the marker it started under, so a clear
/// or a revocation landing mid-fetch wins and a push landing mid-fetch outlives the snapshot — and
/// the session's attempt number, because a same-user reconnect moves neither the cache generation
/// nor the lane epoch: without it an older fetch still in flight re-adds what the reconnect's own
/// reconciliation just proved gone. Capturing that marker, testing the attempt and committing the
/// result all run under the session's own gate — only the fetch runs outside it — so no other
/// operation on the session can land inside one of those units.
public sealed class ServerPermissionFeed : IDisposable {
    readonly PermissionService _permissions;
    readonly SessionDetailReader _readDetail;
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    readonly Lock _attemptLock = new();
    /// The per-session validate-and-commit lane. Internal so a test can hold a session's gate and
    /// pin the ordering rather than race it.
    internal SessionGates CommitGates { get; } = new();
    string? _subject;
    bool _disposed;

    public ServerPermissionFeed(
            IServerLane lane, SessionAccessService access, PermissionService permissions,
            SessionDetailReader readDetail, Func<string, string?> vendorOfSession, TimeProvider time) {
        _permissions = permissions;
        _readDetail = readDetail;

        lane.Status.Where(s => s.State == ServerLaneState.Connected && s.Subject is not null)
            .Subscribe(s => {
                if (_subject is not null && _subject != s.Subject) permissions.ClearServerLane();
                _subject = s.Subject;
            }).DisposeWith(_subscriptions);
        lane.PermissionRequests
            .Subscribe(r => permissions.UpsertServer(PendingPermissionRequest.FromServer(r, vendorOfSession(r.SessionId) ?? "", time.GetUtcNow())))
            .DisposeWith(_subscriptions);
        lane.ElicitationRequests
            .Subscribe(r => permissions.UpsertServer(PendingPermissionRequest.FromServer(r, time.GetUtcNow())))
            .DisposeWith(_subscriptions);
        lane.PermissionResponded
            .Subscribe(p => permissions.SettleServer(p.SessionId, p.RequestId))
            .DisposeWith(_subscriptions);
        // SessionAccessService publishes a transition holding its own lock, and both branches
        // below take the permission cache's — so neither runs on the publishing thread.
        access.Transitions
            .Subscribe(t => {
                // The bump happens where the transition is observed, ahead of the dispatch below,
                // so a transition can never be overtaken by the dispatch it precedes.
                switch (t.State) {
                    case SessionAccessState.Established: {
                        var attempt = Bump(t.SessionId);
                        _ = Task.Run(() => ReconcileAsync(t.SessionId, attempt));
                        break;
                    }
                    // The drop carries the attempt it was raised under, exactly like a
                    // reconciliation: a grant that replaced this denial owns the session's cards,
                    // and dropping them — or moving the cache generation under that grant's own
                    // fetch — would discard a result the server still says is pending.
                    case SessionAccessState.Denied: {
                        var attempt = Bump(t.SessionId);
                        _ = Task.Run(() => CommitAsync(t.SessionId, attempt, () => permissions.DropServerForSession(t.SessionId)));
                        break;
                    }
                }
            }).DisposeWith(_subscriptions);
    }

    int Bump(string sessionId) {
        lock (_attemptLock) return _attempts[sessionId] = _attempts.GetValueOrDefault(sessionId) + 1;
    }

    bool IsCurrent(string sessionId, int attempt) {
        lock (_attemptLock) return _attempts.GetValueOrDefault(sessionId) == attempt;
    }

    /// Tests the attempt and commits under the session's gate, so nothing can move the attempt or
    /// the session's generation between the two. Never spans the fetch itself.
    async Task CommitAsync(string sessionId, int attempt, Action commit) {
        using (await CommitGates.EnterAsync(sessionId).ConfigureAwait(false)) {
            if (!IsCurrent(sessionId, attempt)) return;
            commit();
        }
    }

    /// The marker takes the same gate as the commits. Captured outside it, it names a generation a
    /// drop queued behind this attempt's own test is about to move, and the commit built on it —
    /// whose attempt is still the current one — is then rejected, leaving an authorized session's
    /// cards missing. Null is an attempt superseded before its fetch was worth starting.
    async Task<(int Generation, long Sequence, long Epoch)?> MarkerAsync(string sessionId, int attempt) {
        using (await CommitGates.EnterAsync(sessionId).ConfigureAwait(false))
            return IsCurrent(sessionId, attempt) ? _permissions.SessionMarker(sessionId) : null;
    }

    async Task ReconcileAsync(string sessionId, int attempt) {
        try {
            if (await MarkerAsync(sessionId, attempt).ConfigureAwait(false) is not { } marker) return;
            var fetch = await _readDetail(sessionId, _lifetime.Token).ConfigureAwait(false);
            // A fetch that merely failed says nothing about the session, so its cards stand; only
            // a 404 is evidence there is nothing to hold.
            if (fetch.Detail is null) {
                if (fetch.NotFound)
                    await CommitAsync(sessionId, attempt, () => _permissions.ReplaceServerForSession(sessionId, [], marker)).ConfigureAwait(false);
                return;
            }
            var reconciled = InterruptReconciliation.FromDetail(fetch.Detail);
            IReadOnlyList<PendingPermissionRequest> items = reconciled.Ended
                ? []
                : [.. reconciled.Pending.Where(p => p.IsAnswerableOverHttp)
                    .Select(p => PendingPermissionRequest.FromReconciled(sessionId, p))
                    .OfType<PendingPermissionRequest>()];
            // Tested where the marker is used, not before the fetch: a newer attempt starting
            // between the two would otherwise still lose to this one.
            await CommitAsync(sessionId, attempt, () => _permissions.ReplaceServerForSession(sessionId, items, marker)).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: session reconciliation failed: {ex.Message}");
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        // Every in-flight fetch now reads as superseded rather than applying to a disposed cache.
        lock (_attemptLock) _attempts.Clear();
    }
}
