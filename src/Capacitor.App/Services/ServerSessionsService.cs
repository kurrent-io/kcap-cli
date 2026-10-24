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
/// a connect as another account clears everything first.
public sealed class ServerSessionsService : IServerSessionsService, IDisposable {
    public const int PageSize = 20;
    public const int LivePageSize = 100;
    public const int OffsetCap = 500;
    static readonly TimeSpan DefaultPushDebounce = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan DefaultReconcileEvery = TimeSpan.FromSeconds(60);

    /// A null Ended is a live-only read: the prefix held from the last full one stands.
    sealed record Snapshot(RepoSessionDto[] Live, RepoSessionDto[]? MineLive, RepoSessionDto[]? Ended, int EndedTotal, int LiveNotShown);
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
    readonly BehaviorSubject<ServerSessionsSnapshot> _snapshots = new(ServerSessionsSnapshot.Empty);
    readonly Subject<string> _dropped = new();
    string? _kept;
    RepoSessionDto? _keptRow;
    /// Deletions by arrival order: a read admitted before one still lists the session.
    long _deleteSeq;
    readonly Dictionary<string, long> _tombstones = new(StringComparer.Ordinal);
    TaskCompletionSource _settled = Completed();
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
            }
            _ = RefreshOwnersAsync();
            _ = ReconcileAsync();
        }).DisposeWith(_subscriptions);

        lane.SessionPushes
            .Where(push => push.Kind == SessionPushKind.Deleted)
            .Subscribe(push => {
                lock (_lock) {
                    _live.Remove(push.SessionId);
                    _mineLive.Remove(push.SessionId);
                    _ended.Remove(push.SessionId);
                    _tombstones[push.SessionId] = ++_deleteSeq;
                    if (_kept == push.SessionId) _keptRow = null;
                    EmitSnapshot();
                }
                _dropped.OnNext(push.SessionId);
            }).DisposeWith(_subscriptions);
        var window = pushDebounce ?? DefaultPushDebounce;
        var scheduler = pushScheduler ?? DefaultScheduler.Instance;
        // A live session changes its summary often; only an ending or a deletion can move the ended
        // prefix. Sample, not Throttle, for the frequent kind: a debounce waits for a quiet an active
        // org never gives. Zero is the tests' pass-through, which Sample cannot take as a period.
        var livePushes = lane.SessionPushes.Where(push => push.Kind is SessionPushKind.ActiveAdded or SessionPushKind.ActiveChanged);
        (window == TimeSpan.Zero ? livePushes.Throttle(window, scheduler) : livePushes.Sample(window, scheduler))
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
                if (_scope.Value == value) return;
                _pages = 1;
                _ended.Clear();
                if (value == RailScope.Team) _mineLive.Clear();
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
        lock (_lock) {
            if (_disposed) return Task.CompletedTask;
            if (full) _fullRequested = true;
            if (_busy) {
                _rerun = true;
                return _settled.Task;
            }
            _busy = true;
            _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunAsync();
        return _settled.Task;
    }

    void ReconcileIfConnected() => RequestIfConnected(full: true);

    /// The owners read races the first listing read at connect; a miss there would otherwise leave
    /// the viewer unresolved until the next reconnect.
    void RetryOwnersIfUnresolved() {
        bool retry;
        lock (_lock) retry = _connected && !_disposed && _viewerIds.Value.Count == 0;
        if (retry) _ = RefreshOwnersAsync();
    }

    void RequestIfConnected(bool full) {
        bool connected;
        lock (_lock) connected = _connected && !_disposed;
        if (connected) _ = RequestAsync(full);
    }

    async Task RunAsync() {
        while (true) {
            int generation, laneEpoch, pages;
            long readSeq;
            RailScope scope;
            bool full;
            lock (_lock) {
                generation = _generation;
                laneEpoch = _laneEpoch;
                pages = _pages;
                scope = _scope.Value;
                full = _fullRequested;
                _fullRequested = false;
                readSeq = _deleteSeq;
            }
            Snapshot? snapshot = null;
            var unauthorized = false;
            try {
                (snapshot, unauthorized) = await ReadAsync(scope, pages, full).ConfigureAwait(false);
            } catch (Exception) {
                // A data-plane read that throws is a missed refresh, never an app fault.
            }
            var notifyUnauthorized = false;
            bool rerun;
            TaskCompletionSource? settled = null;
            lock (_lock) {
                // A read taken under another scope or page count answers a question no longer asked,
                // and one admitted while the lane is down answers for a lane that is not there.
                if (generation == _generation && _connected && !_disposed && scope == _scope.Value && pages == _pages) {
                    if (snapshot is not null) Publish(snapshot, pages, readSeq);
                    _current.OnNext(snapshot is not null);
                    if (unauthorized) notifyUnauthorized = true;
                }
                rerun = _rerun;
                if (rerun) {
                    _rerun = false;
                } else {
                    _busy = false;
                    settled = _settled;
                }
            }
            if (notifyUnauthorized) _onUnauthorized?.Invoke(laneEpoch);
            if (rerun) continue;
            settled?.TrySetResult();
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
            var rows = ended.Where(Standing).ToArray();
            _ended.EditDiff(rows, EqualityComparer<RepoSessionDto>.Default);
            var atCap = pages * PageSize >= OffsetCap;
            _paging.OnNext(new(rows.Length, snapshot.EndedTotal, pages, rows.Length < snapshot.EndedTotal && !atCap, atCap));
        }
        foreach (var id in _tombstones.Where(t => t.Value <= readSeq).Select(t => t.Key).ToList()) _tombstones.Remove(id);
        _liveNotShown.OnNext(snapshot.LiveNotShown);
        EmitSnapshot();
    }

    async Task<(Snapshot? Snapshot, bool Unauthorized)> ReadAsync(RailScope scope, int pages, bool full) {
        var owner = scope == RailScope.Mine ? "me" : null;
        var (scoped, unauthorized) = await ReadLiveAsync(owner).ConfigureAwait(false);
        if (scoped is null) return (null, unauthorized);
        var all = scoped;
        if (owner is not null) {
            (all, unauthorized) = await ReadLiveAsync(null).ConfigureAwait(false);
            if (all is null) return (null, unauthorized);
        }
        var liveNotShown = Math.Max(0, scoped.Total - scoped.Rows.Length);
        if (!full) return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, null, 0, liveNotShown), false);
        var ended = new List<RepoSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        for (var page = 0; page < pages; page++) {
            var offset = page * PageSize;
            if (offset > OffsetCap) break;
            var result = await _fetch(new ServerSessionsQuery("ended", owner, PageSize, offset), CancellationToken.None).ConfigureAwait(false);
            if (result.Rows is null) return (null, result.Unauthorized);
            total = result.Total;
            foreach (var row in result.Rows) if (seen.Add(row.SessionId)) ended.Add(row);
            if (result.Rows.Length < PageSize) break;
        }
        return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, [.. ended], total, liveNotShown), false);
    }

    async Task<(LiveRead? Read, bool Unauthorized)> ReadLiveAsync(string? owner) {
        var rows = new List<RepoSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        for (var offset = 0; offset <= OffsetCap; offset += LivePageSize) {
            var result = await _fetch(new ServerSessionsQuery("active", owner, LivePageSize, offset), CancellationToken.None).ConfigureAwait(false);
            if (result.Rows is null) return (null, result.Unauthorized);
            total = result.Total;
            foreach (var row in result.Rows) if (seen.Add(row.SessionId)) rows.Add(row);
            if (rows.Count >= total || result.Rows.Length < LivePageSize) break;
        }
        return (new LiveRead([.. rows], total), false);
    }

    async Task RefreshOwnersAsync() {
        int generation;
        lock (_lock) generation = _generation;
        SessionOwnerOptionDto[]? owners = null;
        try {
            owners = await _fetchOwners(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception) {
        }
        if (owners is null) return;
        var me = owners.FirstOrDefault(o => o.IsMe);
        IReadOnlySet<string> ids = me is null ? FrozenSet<string>.Empty
            : (me.Ids is { Length: > 0 } aliases ? aliases.Append(me.UserId) : [me.UserId]).ToFrozenSet(StringComparer.Ordinal);
        lock (_lock) {
            if (generation != _generation || _disposed) return;
            _viewerIds.OnNext(ids);
            _viewerUserId.OnNext(me?.UserId);
        }
    }

    /// Nothing listed under the previous account survives into the next one.
    void ClearForNewAccount() {
        var gone = _live.Keys.Concat(_mineLive.Keys).Concat(_ended.Keys).Append(_kept)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
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
        foreach (var id in gone) _dropped.OnNext(id);
    }

    static TaskCompletionSource Completed() {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
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
        TaskCompletionSource settled;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            settled = _settled;
        }
        _subscriptions.Dispose();
        _cadence.Dispose();
        settled.TrySetResult();
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
    }
}
