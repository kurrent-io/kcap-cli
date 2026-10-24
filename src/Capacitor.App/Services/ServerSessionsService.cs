using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
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

    sealed record Snapshot(RepoSessionDto[] Live, RepoSessionDto[]? MineLive, RepoSessionDto[] Ended, int EndedTotal, int LiveNotShown);
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
    TaskCompletionSource _settled = Completed();
    bool _disposed;

    public ServerSessionsService(
            IServerLane lane,
            Func<ServerSessionsQuery, CancellationToken, Task<ServerSessionsFetch>> fetch,
            Func<CancellationToken, Task<SessionOwnerOptionDto[]?>> fetchOwners,
            TimeProvider time,
            TimeSpan? pushDebounce = null, TimeSpan? reconcileEvery = null, Action<int>? onUnauthorized = null) {
        _fetch = fetch;
        _fetchOwners = fetchOwners;
        _onUnauthorized = onUnauthorized;

        lane.Status.Subscribe(status => {
            if (status.State != ServerLaneState.Connected) {
                lock (_lock) _connected = false;
                _current.OnNext(false);
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
                }
            }).DisposeWith(_subscriptions);
        lane.SessionPushes
            .Throttle(pushDebounce ?? DefaultPushDebounce)
            .Subscribe(_ => ReconcileIfConnected()).DisposeWith(_subscriptions);

        var every = reconcileEvery ?? DefaultReconcileEvery;
        _cadence = time.CreateTimer(_ => ReconcileIfConnected(), null, every, every);
    }

    public IObservableCache<RepoSessionDto, string> Live => _live.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> MineLive => _mineLive.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> Ended => _ended.AsObservableCache();
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
                _paging.OnNext(new(0, 0, 1, false, false));
                _scope.OnNext(value);
            }
            ReconcileIfConnected();
        }
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
    public Task ReconcileAsync() {
        lock (_lock) {
            if (_disposed) return Task.CompletedTask;
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

    void ReconcileIfConnected() {
        bool connected;
        lock (_lock) connected = _connected && !_disposed;
        if (connected) _ = ReconcileAsync();
    }

    async Task RunAsync() {
        while (true) {
            int generation, laneEpoch, pages;
            RailScope scope;
            lock (_lock) {
                generation = _generation;
                laneEpoch = _laneEpoch;
                pages = _pages;
                scope = _scope.Value;
            }
            Snapshot? snapshot = null;
            var unauthorized = false;
            try {
                (snapshot, unauthorized) = await ReadAsync(scope, pages).ConfigureAwait(false);
            } catch (Exception) {
                // A data-plane read that throws is a missed refresh, never an app fault.
            }
            var notifyUnauthorized = false;
            bool? current = null;
            bool rerun;
            TaskCompletionSource? settled = null;
            lock (_lock) {
                if (generation == _generation && !_disposed) {
                    if (snapshot is not null) {
                        Publish(snapshot, pages);
                        current = true;
                    } else {
                        current = false;
                    }
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
            if (current is { } evidence && !_disposed) _current.OnNext(evidence);
            if (notifyUnauthorized) _onUnauthorized?.Invoke(laneEpoch);
            if (rerun) continue;
            settled?.TrySetResult();
            return;
        }
    }

    void Publish(Snapshot snapshot, int pages) {
        _live.EditDiff(snapshot.Live, EqualityComparer<RepoSessionDto>.Default);
        if (snapshot.MineLive is { } mine) _mineLive.EditDiff(mine, EqualityComparer<RepoSessionDto>.Default);
        else _mineLive.Clear();
        _ended.EditDiff(snapshot.Ended, EqualityComparer<RepoSessionDto>.Default);
        var atCap = pages * PageSize >= OffsetCap;
        _paging.OnNext(new(snapshot.Ended.Length, snapshot.EndedTotal, pages, snapshot.Ended.Length < snapshot.EndedTotal && !atCap, atCap));
        _liveNotShown.OnNext(snapshot.LiveNotShown);
    }

    async Task<(Snapshot? Snapshot, bool Unauthorized)> ReadAsync(RailScope scope, int pages) {
        var owner = scope == RailScope.Mine ? "me" : null;
        var (scoped, unauthorized) = await ReadLiveAsync(owner).ConfigureAwait(false);
        if (scoped is null) return (null, unauthorized);
        var all = scoped;
        if (owner is not null) {
            (all, unauthorized) = await ReadLiveAsync(null).ConfigureAwait(false);
            if (all is null) return (null, unauthorized);
        }
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
        return (new Snapshot(all.Rows, owner is null ? null : scoped.Rows, [.. ended], total, Math.Max(0, scoped.Total - scoped.Rows.Length)), false);
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
        _live.Clear();
        _mineLive.Clear();
        _ended.Clear();
        _pages = 1;
        _paging.OnNext(new(0, 0, 1, false, false));
        _liveNotShown.OnNext(0);
        _viewerIds.OnNext(FrozenSet<string>.Empty);
        _viewerUserId.OnNext(null);
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
    }
}
