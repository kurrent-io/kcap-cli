using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// Reads the listing on connect, on every session push and on a slow cadence, one reconciliation
/// at a time: a request admitted while one runs reruns it, so the caches always hold one read's
/// result. A connect bumps the generation and a completion from an older one publishes nothing;
/// a connect as another account clears everything first. A refused page holds every read until the
/// server's retry time, and the pages a reconcile did read before it are kept.
public sealed class ServerSessionsService : IServerSessionsService, IDisposable {
    public const int PageSize = 20;
    public const int LivePageSize = 100;
    public const int OffsetCap = 500;
    static readonly TimeSpan DefaultPushDebounce = TimeSpan.FromMilliseconds(250);
    /// The server pushes every live session of the tenant to every client, and a live re-read is
    /// up to six listing pages, so a busy org gets one re-read per window from each open app.
    static readonly TimeSpan DefaultLiveSample = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DefaultReconcileEvery = TimeSpan.FromSeconds(60);
    /// A refused reconcile goes again once its hold lapses; a zero Retry-After would make that a spin.
    static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(1);

    /// A null Ended is a live-only read: the prefix held from the last full one stands. A partial
    /// Ended stopped at a failed page, so it says nothing of the rows past the pages it read.
    sealed record Snapshot(RepoSessionDto[] Live, RepoSessionDto[]? MineLive, RepoSessionDto[]? Ended, int EndedTotal, int LiveNotShown,
        bool EndedPartial = false);
    sealed record LiveRead(RepoSessionDto[] Rows, int Total);

    readonly SourceCache<RepoSessionDto, string> _live = new(r => r.SessionId);
    readonly SourceCache<RepoSessionDto, string> _mineLive = new(r => r.SessionId);
    readonly SourceCache<RepoSessionDto, string> _ended = new(r => r.SessionId);
    readonly BehaviorSubject<ServerSessionsPaging> _paging = new(new(0, 0, 1, false, false));
    readonly BehaviorSubject<int> _liveNotShown = new(0);
    readonly BehaviorSubject<IReadOnlySet<string>> _viewerIds = new(FrozenSet<string>.Empty);
    readonly BehaviorSubject<string?> _viewerUserId = new(null);
    readonly BehaviorSubject<bool> _current = new(false);
    readonly BehaviorSubject<RailScope> _scope = new(RailScope.Mine);
    readonly Func<ServerSessionsQuery, CancellationToken, Task<ServerSessionsFetch>> _fetch;
    readonly Func<CancellationToken, Task<SessionOwnerOptionDto[]?>> _fetchOwners;
    readonly Action<int>? _onUnauthorized;
    readonly CompositeDisposable _subscriptions = new();
    readonly ITimer _cadence;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _disposing = new();
    // Guards every field below and each publish, so a connect's clear and bump cannot land between
    // a read's generation check and its publish.
    readonly Lock _lock = new();
    int _generation;
    string? _lastSubject;
    int _laneEpoch;
    bool _connected;
    int _pages = 1;
    bool _busy;
    bool _rerun;
    bool _fullRequested;
    bool _ownersBusy;
    /// The owners endpoint answered once this connection; an account with no entry of its own is resolved too.
    bool _ownersResolved;
    /// No listing read goes out before this: the server refused one for the user's read budget. It is
    /// that account's budget, so another account's read neither sets nor waits on it.
    DateTimeOffset _heldUntil = DateTimeOffset.MinValue;
    int _account;
    /// Cancelled when the account changes, so a read waiting on the previous account's hold goes at once.
    CancellationTokenSource _holdWake = new();
    readonly BehaviorSubject<ServerSessionsSnapshot> _snapshots = new(ServerSessionsSnapshot.Empty);
    readonly Subject<string> _dropped = new();
    string? _kept;
    RepoSessionDto? _keptRow;
    /// Deletions by arrival order: a read admitted before one still lists the session.
    long _deleteSeq;
    readonly Dictionary<string, long> _tombstones = new(StringComparer.Ordinal);
    /// Requests awaiting the next admitted read, and those the read in progress answers: a request
    /// is settled by its own read, whatever refreshes queue behind it.
    List<TaskCompletionSource> _waiting = [];
    List<TaskCompletionSource> _serving = [];
    bool _disposed;

    public ServerSessionsService(
            IServerLane lane,
            Func<ServerSessionsQuery, CancellationToken, Task<ServerSessionsFetch>> fetch,
            Func<CancellationToken, Task<SessionOwnerOptionDto[]?>> fetchOwners,
            TimeProvider time,
            TimeSpan? pushDebounce = null, TimeSpan? reconcileEvery = null, Action<int>? onUnauthorized = null,
            IScheduler? pushScheduler = null) {
        _fetch = fetch;
        _fetchOwners = fetchOwners;
        _time = time;
        _onUnauthorized = onUnauthorized;

        lane.Status.Subscribe(status => {
            if (status.State != ServerLaneState.Connected) {
                // A read that outlives its connection answers for a lane that is gone.
                lock (_lock) {
                    _connected = false;
                    _generation++;
                    _current.OnNext(false);
                }
                return;
            }
            lock (_lock) {
                if (status.Subject is not null && _lastSubject is not null && _lastSubject != status.Subject) ClearForNewAccount();
                if (status.Subject is not null) _lastSubject = status.Subject;
                _generation++;
                _laneEpoch = status.Epoch;
                _connected = true;
                _ownersResolved = false;
            }
            _ = RefreshOwnersAsync();
            _ = ReconcileAsync();
        }).DisposeWith(_subscriptions);

        lane.SessionPushes
            .Where(push => push.Kind == SessionPushKind.Deleted)
            .Subscribe(push => {
                // The drop is announced before the snapshot without the row, so a consumer acting
                // on the removal alone does not mistake a daemon row it exposes for a takeover.
                _dropped.OnNext(push.SessionId);
                lock (_lock) {
                    _live.Remove(push.SessionId);
                    _mineLive.Remove(push.SessionId);
                    _ended.Remove(push.SessionId);
                    _tombstones[push.SessionId] = ++_deleteSeq;
                    if (_kept == push.SessionId) _keptRow = null;
                    EmitSnapshot();
                }
            }).DisposeWith(_subscriptions);
        var window = pushDebounce ?? DefaultPushDebounce;
        var liveWindow = pushDebounce ?? DefaultLiveSample;
        var scheduler = pushScheduler ?? DefaultScheduler.Instance;
        // A live session changes its summary often; only an ending or a deletion can move the ended
        // prefix. Sample, not Throttle, for the frequent kind: a debounce waits for a quiet an active
        // org never gives. Zero is the tests' pass-through, which Sample cannot take as a period.
        var livePushes = lane.SessionPushes.Where(push => push.Kind is SessionPushKind.ActiveAdded or SessionPushKind.ActiveChanged);
        (liveWindow == TimeSpan.Zero ? livePushes.Throttle(liveWindow, scheduler) : livePushes.Sample(liveWindow, scheduler))
            .Subscribe(_ => RequestIfConnected(full: false)).DisposeWith(_subscriptions);
        lane.SessionPushes
            .Where(push => push.Kind is SessionPushKind.ActiveRemoved or SessionPushKind.TitleChanged or SessionPushKind.Deleted)
            .Throttle(window, scheduler)
            .Subscribe(_ => RequestIfConnected(full: true)).DisposeWith(_subscriptions);

        var every = reconcileEvery ?? DefaultReconcileEvery;
        _cadence = time.CreateTimer(_ => { ReconcileIfConnected(); RetryOwnersIfUnresolved(); }, null, every, every);
    }

    public IObservableCache<RepoSessionDto, string> Live => _live.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> MineLive => _mineLive.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> Ended => _ended.AsObservableCache();
    public IObservable<ServerSessionsSnapshot> Snapshots => _snapshots.AsObservable();
    public IObservable<string> Dropped => _dropped.AsObservable();
    public IObservable<ServerSessionsPaging> Paging => _paging.AsObservable();
    public IObservable<int> LiveNotShown => _liveNotShown.AsObservable();
    public IObservable<IReadOnlySet<string>> ViewerIds => _viewerIds.AsObservable();
    public IObservable<string?> ViewerUserId => _viewerUserId.AsObservable();
    public IObservable<bool> Current => _current.AsObservable();
    public IObservable<RailScope> ScopeChanges => _scope.AsObservable();

    public RailScope Scope {
        get => _scope.Value;
        set {
            lock (_lock) {
                if (_disposed || _scope.Value == value) return;
                _pages = 1;
                _ended.Clear();
                // Under Mine the unfiltered live set is ownership evidence; the previous scope's copy
                // would hide the viewer's own registry rows as listed-but-not-mine until the read lands.
                if (value == RailScope.Team) _mineLive.Clear();
                else _live.Clear();
                // Retention exempts the open row from the page cut, not from whose sessions the scope shows.
                _keptRow = null;
                _paging.OnNext(new(0, 0, 1, false, false));
                _scope.OnNext(value);
                EmitSnapshot();
            }
            ReconcileIfConnected();
        }
    }

    public void Keep(string? sessionId) {
        lock (_lock) {
            if (_disposed || _kept == sessionId) return;
            _kept = sessionId;
            _keptRow = sessionId is null ? null : Listed(sessionId);
            EmitSnapshot();
        }
    }

    RepoSessionDto? Listed(string sessionId) =>
        _ended.Lookup(sessionId) is { HasValue: true, Value: var ended } ? ended
        : _live.Lookup(sessionId) is { HasValue: true, Value: var live } ? live
        : null;

    /// Under the lock, after every cache change. The kept row rides along from the last read that
    /// listed it once the current one does not.
    void EmitSnapshot() {
        var ended = _ended.Items.ToList();
        if (_kept is { } kept) {
            if (Listed(kept) is { } current) _keptRow = current;
            else if (_keptRow is { } retained) ended.Add(retained);
        }
        _snapshots.OnNext(new([.. _live.Items], [.. _mineLive.Items], ended));
    }

    public Task ShowMoreAsync() {
        lock (_lock) {
            if (_pages * PageSize >= OffsetCap) return Task.CompletedTask;
            _pages++;
        }
        return ReconcileAsync();
    }

    /// Admission and completion share the lock, so a request that loses admission is never
    /// dropped between the loop reading the rerun flag and clearing busy.
    public Task ReconcileAsync() => RequestAsync(full: true);

    /// A full request stays requested until a full read starts, so a live-only run in flight reruns full.
    Task RequestAsync(bool full) {
        TaskCompletionSource waiter;
        lock (_lock) {
            if (_disposed) return Task.CompletedTask;
            if (full) _fullRequested = true;
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add(waiter);
            if (_busy) {
                _rerun = true;
                return waiter.Task;
            }
            _busy = true;
        }
        _ = RunAsync();
        return waiter.Task;
    }

    void ReconcileIfConnected() => RequestIfConnected(full: true);

    /// The owners read races the first listing read at connect; a miss there would otherwise leave
    /// the viewer unresolved until the next reconnect.
    void RetryOwnersIfUnresolved() {
        bool retry;
        lock (_lock) retry = _connected && !_disposed && !_ownersResolved && !_ownersBusy;
        if (retry) _ = RefreshOwnersAsync();
    }

    void RequestIfConnected(bool full) {
        bool connected;
        lock (_lock) connected = _connected && !_disposed;
        if (connected) _ = RequestAsync(full);
    }

    async Task RunAsync() {
        while (true) {
            TimeSpan hold;
            CancellationToken wake;
            lock (_lock) {
                hold = _heldUntil - _time.GetUtcNow();
                wake = _holdWake.Token;
            }
            if (hold > TimeSpan.Zero) {
                // Still busy while held, so every request in the meantime folds into the one read after.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token, wake);
                try {
                    await Task.Delay(hold, _time, linked.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!_disposing.IsCancellationRequested) {
                    // Woken by the account change under its lock; the read starts off that thread.
                    await Task.Yield();
                } catch (OperationCanceledException) {
                    return;
                }
            }
            int generation, laneEpoch, pages, account;
            long readSeq;
            RailScope scope;
            bool full;
            lock (_lock) {
                generation = _generation;
                account = _account;
                laneEpoch = _laneEpoch;
                pages = _pages;
                scope = _scope.Value;
                full = _fullRequested;
                _fullRequested = false;
                readSeq = _deleteSeq;
                // Every request so far is served by this read, those that arrived during a hold too.
                _rerun = false;
                _serving = _waiting;
                _waiting = [];
            }
            Snapshot? snapshot = null;
            var unauthorized = false;
            TimeSpan? refused = null;
            try {
                (snapshot, unauthorized, refused) = await ReadAsync(scope, pages, full).ConfigureAwait(false);
            } catch (Exception) {
                // A data-plane read that throws is a missed refresh, never an app fault.
            }
            var notifyUnauthorized = false;
            bool rerun;
            List<TaskCompletionSource> served;
            lock (_lock) {
                // A read taken under another scope or page count answers a question no longer asked,
                // and one admitted while the lane is down answers for a lane that is not there.
                if (generation == _generation && _connected && !_disposed && scope == _scope.Value && pages == _pages) {
                    if (snapshot is not null) Publish(snapshot, pages, readSeq);
                    _current.OnNext(snapshot is { EndedPartial: false });
                    if (unauthorized) notifyUnauthorized = true;
                }
                if (refused is { } wait && account == _account) {
                    var until = _time.GetUtcNow() + (wait > MinimumHold ? wait : MinimumHold);
                    if (until > _heldUntil) _heldUntil = until;
                    // A refused reconcile goes again once the hold lapses; a live read waits for the next push.
                    if (full) {
                        _fullRequested = true;
                        _rerun = true;
                    }
                }
                rerun = _rerun && !_disposed;
                if (rerun) _rerun = false;
                else _busy = false;
                served = _serving;
                _serving = [];
            }
            foreach (var waiter in served) waiter.TrySetResult();
            if (notifyUnauthorized) _onUnauthorized?.Invoke(laneEpoch);
            if (rerun) continue;
            return;
        }
    }

    void Publish(Snapshot snapshot, int pages, long readSeq) {
        // A deletion pushed after this read was admitted outranks what the read still lists.
        bool Standing(RepoSessionDto row) => !(_tombstones.TryGetValue(row.SessionId, out var seq) && seq > readSeq);
        _live.EditDiff(snapshot.Live.Where(Standing), EqualityComparer<RepoSessionDto>.Default);
        if (snapshot.MineLive is { } mine) _mineLive.EditDiff(mine.Where(Standing), EqualityComparer<RepoSessionDto>.Default);
        else _mineLive.Clear();
        if (snapshot.Ended is { } ended) {
            var rows = ended.Where(Standing).ToList();
            var total = snapshot.EndedTotal;
            if (snapshot.EndedPartial) {
                // The pages past the failed one were not read: what they held stands, unless the
                // session is live again.
                var read = rows.Select(r => r.SessionId).Concat(snapshot.Live.Select(r => r.SessionId))
                    .Concat(snapshot.MineLive?.Select(r => r.SessionId) ?? []).ToHashSet(StringComparer.Ordinal);
                rows.AddRange(_ended.Items.Where(r => !read.Contains(r.SessionId)));
                if (ended.Length == 0) total = _paging.Value.Total;
            }
            _ended.EditDiff(rows, EqualityComparer<RepoSessionDto>.Default);
            var atCap = pages * PageSize >= OffsetCap;
            _paging.OnNext(new(rows.Count, total, pages, rows.Count < total && !atCap, atCap));
        }
        foreach (var id in _tombstones.Where(t => t.Value <= readSeq).Select(t => t.Key).ToList()) _tombstones.Remove(id);
        _liveNotShown.OnNext(snapshot.LiveNotShown);
        EmitSnapshot();
    }

    async Task<(Snapshot? Snapshot, bool Unauthorized, TimeSpan? RetryAfter)> ReadAsync(RailScope scope, int pages, bool full) {
        var owner = scope == RailScope.Mine ? "me" : null;
        var (scoped, failure) = await ReadLiveAsync(owner).ConfigureAwait(false);
        if (scoped is null) return (null, failure.Unauthorized, failure.RetryAfter);
        var all = scoped;
        if (owner is not null) {
            (all, failure) = await ReadLiveAsync(null).ConfigureAwait(false);
            if (all is null) return (null, failure.Unauthorized, failure.RetryAfter);
        }
        var liveNotShown = Math.Max(0, scoped.Total - scoped.Rows.Length);
        if (!full) return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, null, 0, liveNotShown), false, null);
        var ended = new List<RepoSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        for (var page = 0; page < pages; page++) {
            var offset = page * PageSize;
            if (offset > OffsetCap) break;
            var result = await _fetch(new ServerSessionsQuery("ended", owner, PageSize, offset), CancellationToken.None).ConfigureAwait(false);
            if (result.Unauthorized) return (null, true, null);
            if (result.Rows is null)
                return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, [.. ended], total, liveNotShown, EndedPartial: true), false, result.RetryAfter);
            total = result.Total;
            foreach (var row in result.Rows) if (seen.Add(row.SessionId)) ended.Add(row);
            if (result.Rows.Length < PageSize) break;
        }
        return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, [.. ended], total, liveNotShown), false, null);
    }

    async Task<(LiveRead? Read, ServerSessionsFetch Failure)> ReadLiveAsync(string? owner) {
        var rows = new List<RepoSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        for (var offset = 0; offset <= OffsetCap; offset += LivePageSize) {
            var result = await _fetch(new ServerSessionsQuery("active", owner, LivePageSize, offset), CancellationToken.None).ConfigureAwait(false);
            if (result.Rows is null) return (null, result);
            total = result.Total;
            foreach (var row in result.Rows) if (seen.Add(row.SessionId)) rows.Add(row);
            if (rows.Count >= total || result.Rows.Length < LivePageSize) break;
        }
        return (new LiveRead([.. rows], total), new(null, 0));
    }

    async Task RefreshOwnersAsync() {
        int generation;
        lock (_lock) {
            if (_ownersBusy) return;
            _ownersBusy = true;
            generation = _generation;
        }
        SessionOwnerOptionDto[]? owners = null;
        try {
            owners = await _fetchOwners(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception) {
        }
        bool stale;
        lock (_lock) {
            _ownersBusy = false;
            stale = generation != _generation;
        }
        // A reconnect while this read was pending found it busy and skipped its own; this starts it.
        if (stale) {
            RetryOwnersIfUnresolved();
            return;
        }
        if (owners is null) return;
        var me = owners.FirstOrDefault(o => o.IsMe);
        IReadOnlySet<string> ids = me is null ? FrozenSet<string>.Empty
            : (me.Ids is { Length: > 0 } aliases ? aliases.Append(me.UserId) : [me.UserId]).ToFrozenSet(StringComparer.Ordinal);
        lock (_lock) {
            if (_disposed) return;
            _ownersResolved = true;
            _viewerIds.OnNext(ids);
            _viewerUserId.OnNext(me?.UserId);
        }
    }

    /// Nothing listed under the previous account survives into the next one.
    void ClearForNewAccount() {
        _account++;
        _heldUntil = DateTimeOffset.MinValue;
        _holdWake.Cancel();
        _holdWake.Dispose();
        _holdWake = new();
        var gone = _live.Keys.Concat(_mineLive.Keys).Concat(_ended.Keys).Append(_kept)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in gone) _dropped.OnNext(id);
        _live.Clear();
        _mineLive.Clear();
        _ended.Clear();
        _pages = 1;
        _paging.OnNext(new(0, 0, 1, false, false));
        _liveNotShown.OnNext(0);
        _viewerIds.OnNext(FrozenSet<string>.Empty);
        _viewerUserId.OnNext(null);
        _kept = null;
        _keptRow = null;
        _tombstones.Clear();
        EmitSnapshot();
    }

    /// Authenticated GET of one listing page through the waiting verb, so a token the hub refreshed
    /// is what HTTP sends. Lane loss is data: an unreachable or non-auth failure is a null page.
    public static Func<ServerSessionsQuery, CancellationToken, Task<ServerSessionsFetch>> HttpFetch(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async (query, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new(null, 0);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(null, 0, Unauthorized: true);
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.SessionListing(query.State, query.Owner, query.Limit, query.Offset)}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, 0, Unauthorized: true);
                // The server-wide cap answers a one-second wait, the shortest a refusal can ask for.
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return new(null, 0, RetryAfter: RetryAfterHeader.Read(response, TimeProvider.System) ?? TimeSpan.FromSeconds(1));
                if (!response.IsSuccessStatusCode) return new(null, 0);
                var page = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.RepoSessionsResponseDto, ct).ConfigureAwait(false);
                return page is null ? new(null, 0) : new(page.Items, page.Total);
            }
        } catch (Exception) {
            return new(null, 0);
        }
    };

    public static Func<CancellationToken, Task<SessionOwnerOptionDto[]?>> HttpOwners(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.SessionOwners}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.SessionOwnerOptionDtoArray, ct).ConfigureAwait(false);
            }
        } catch (Exception) {
            return null;
        }
    };

    public void Dispose() {
        List<TaskCompletionSource> waiters;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            waiters = [.. _waiting, .. _serving];
            _waiting = [];
            _serving = [];
        }
        _disposing.Cancel();
        _subscriptions.Dispose();
        _cadence.Dispose();
        foreach (var waiter in waiters) waiter.TrySetResult();
        _live.Dispose();
        _mineLive.Dispose();
        _ended.Dispose();
        _paging.Dispose();
        _liveNotShown.Dispose();
        _viewerIds.Dispose();
        _viewerUserId.Dispose();
        _current.Dispose();
        _scope.Dispose();
        _snapshots.Dispose();
        _dropped.Dispose();
        _disposing.Dispose();
        _holdWake.Dispose();
    }
}
