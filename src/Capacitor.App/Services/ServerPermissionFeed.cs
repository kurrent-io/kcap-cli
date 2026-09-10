using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// Feeds the permission cache's server lane: live pushes for sessions the app has joined, a
/// reconciliation of each session's event stream every time its access is established, and the
/// org-wide settlement pings. Every reconciliation carries the session generation it started
/// under, so a clear that lands mid-fetch wins.
public sealed class ServerPermissionFeed : IDisposable {
    readonly PermissionService _permissions;
    readonly SessionDetailReader _readDetail;
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime = new();
    string? _subject;

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
                switch (t.State) {
                    case SessionAccessState.Established: _ = Task.Run(() => ReconcileAsync(t.SessionId)); break;
                    case SessionAccessState.Denied: _ = Task.Run(() => permissions.DropServerForSession(t.SessionId)); break;
                }
            }).DisposeWith(_subscriptions);
    }

    async Task ReconcileAsync(string sessionId) {
        var generation = _permissions.SessionGeneration(sessionId);
        SessionDetailFetch fetch;
        try { fetch = await _readDetail(sessionId, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: session reconciliation failed: {ex.Message}"); return; }

        if (fetch.Detail is null) {
            if (fetch.NotFound) _permissions.ReplaceServerForSession(sessionId, [], generation);
            return;
        }
        var reconciled = InterruptReconciliation.FromDetail(fetch.Detail);
        IReadOnlyList<PendingPermissionRequest> items = reconciled.Ended
            ? []
            : [.. reconciled.Pending.Where(p => p.IsAnswerableOverHttp)
                .Select(p => PendingPermissionRequest.FromReconciled(sessionId, p))
                .OfType<PendingPermissionRequest>()];
        _permissions.ReplaceServerForSession(sessionId, items, generation);
    }

    public void Dispose() {
        _subscriptions.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
