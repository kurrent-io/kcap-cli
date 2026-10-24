using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IRemoteAgentsService {
    /// Keyed by AgentId. Retained across lane loss — staleness is presentation.
    IObservableCache<AgentInstanceDto, string> Agents { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; }
    /// Replay-1, seeded unanswered at generation 0.
    IObservable<DaemonListing> Listing { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts { get; }
    /// Fetches the daemon listing now, ahead of any retry the backoff is waiting on.
    Task RefreshDaemonsAsync();
}

/// An agents fetch's outcome: Rows is null on any failure (including Unauthorized), so a caller
/// that only cares about data can ignore Unauthorized entirely; Unauthorized additionally
/// distinguishes the one failure kind that needs to reach the sign-in surface rather than being
/// swallowed as ordinary lane loss.
public sealed record RemoteFetch(AgentInstanceDto[]? Rows, bool Unauthorized = false);

/// Remote registry caches: seeded on lane Connected, refreshed on the org-wide pings. A failed
/// fetch returns null Rows and leaves the caches as they were — lane loss is data — except
/// Unauthorized, which additionally invokes onUnauthorized so the sign-in surface hears about it.
public sealed class RemoteAgentsService : IRemoteAgentsService, IDisposable {
    readonly SourceCache<AgentInstanceDto, string> _agents = new(a => a.AgentId);
    readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> _daemons = new([]);
    readonly BehaviorSubject<DaemonListing> _listing = new(new(null, 0, false));
    readonly BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> _daemonAccounts = new([]);
    readonly IDisposable _subscriptions;
    static readonly TimeSpan DaemonsRefreshTimeout = TimeSpan.FromSeconds(5);
    // The listing otherwise refreshes only on a connect or a daemons push, so one failed fetch on a
    // healthy connection would leave it unanswered, and account support unknown, until the next.
    static readonly TimeSpan[] ListingRetryDelays = [
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(24),
        TimeSpan.FromSeconds(30),
    ];

    // Guards the mutable fields below. _generation bumps on every Connected status carrying a
    // subject, so a fetch issued before the latest connect is stale for both publishing and
    // onUnauthorized. _listingGeneration bumps on every Connected status, subject or not: the
    // local edition connects without one, and its listing must still read unanswered after a
    // reconnect. Admission (busy check / set rerun) and completion (rerun check / clear busy)
    // share one critical section, so a losing admission can never land between a drain loop
    // reading rerun false and clearing busy.
    readonly Lock _lock = new();
    int _generation;
    int _listingGeneration;
    string? _lastSubject;
    // The lane's own Epoch off the latest Connected status seen — captured per fetch and handed
    // to onUnauthorized, so ServerConnectionService.ParkSignedOut(int) can re-validate a delayed
    // park decision against the lane's CURRENT epoch rather than trusting this generation check
    // alone, whose lock releases before the callback runs.
    int _lastLaneEpoch;
    bool _laneConnected;
    bool _disposed;
    bool _agentsBusy;
    bool _agentsRerun;
    bool _daemonsBusy;
    bool _daemonsRerun;
    bool _daemonAccountsBusy;
    bool _daemonAccountsRerun;
    TaskCompletionSource? _daemonsInFlight;
    ITimer? _listingRetry;
    long _listingRetryTicket;
    int _listingRetryAttempt;
    readonly IServerLane _lane;
    readonly Action<int>? _onUnauthorized;
    readonly TimeProvider _time;

    public RemoteAgentsService(
            IServerLane lane, Func<CancellationToken, Task<RemoteFetch>> fetchAgents,
            TimeSpan? debounce = null, Action<int>? onUnauthorized = null, TimeProvider? time = null) {
        _lane = lane;
        _onUnauthorized = onUnauthorized;
        _time = time ?? TimeProvider.System;
        var wait = debounce ?? TimeSpan.FromMilliseconds(250);
        var connected = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Where(c => c)
            .Select(_ => System.Reactive.Unit.Default);
        // Not an edge: the lane can publish Connected twice in a row, and each one resets the
        // listing and supersedes the fetch in flight, so each needs a fetch of its own.
        var everyConnect = lane.Status
            .Where(s => s.State == ServerLaneState.Connected)
            .Select(_ => System.Reactive.Unit.Default);

        // Subscribed before the refresh triggers below (same lane.Status), so on a Connected
        // status this runs first: nothing seeded under one identity survives into another, even
        // when the refresh that follows fetches null and would otherwise leave stale rows in
        // place, and the listing reads unanswered before the new generation's fetch starts.
        var laneStatus = lane.Status.Subscribe(s => {
            lock (_lock) {
                _laneConnected = s.State == ServerLaneState.Connected;
                ResetListingRetry();
                if (!_laneConnected) return;
                if (s.Subject is not null) {
                    if (_lastSubject is not null && _lastSubject != s.Subject) {
                        _agents.Clear();
                        _daemons.OnNext([]);
                        _daemonAccounts.OnNext([]);
                    }
                    _generation++;
                    _lastSubject = s.Subject;
                }
                _listingGeneration++;
                _listing.OnNext(new(null, _listingGeneration, false));
                _lastLaneEpoch = s.Epoch;
            }
        });

        // Merge, not Concat: triggers must reach the refresh methods concurrently so a busy
        // refresh's admission check can actually observe contention and coalesce. Daemons
        // subscribes before Agents so a Connected edge starts the daemon refresh first and
        // RefreshAgentsAsync can await its completion before publishing rows that AgentDirectory
        // deduplicates against Daemons.
        var refreshDaemons = everyConnect.Merge(lane.DaemonsChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(() => RefreshDaemonsAsync()))
            .Merge()
            .Subscribe();
        var refreshDaemonAccounts = everyConnect.Merge(lane.DaemonAccountsChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(() => RefreshDaemonAccountsAsync()))
            .Merge()
            .Subscribe();
        var refreshAgents = connected.Merge(lane.AgentInstancesChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(async () => await RefreshAgentsAsync(fetchAgents)))
            .Merge()
            .Subscribe();
        _subscriptions = new System.Reactive.Disposables.CompositeDisposable(
            laneStatus, refreshDaemons, refreshDaemonAccounts, refreshAgents);
    }

    public IObservableCache<AgentInstanceDto, string> Agents => _agents.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => _daemons.AsObservable();
    public IObservable<DaemonListing> Listing => _listing.AsObservable();
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts => _daemonAccounts.AsObservable();

    async Task RefreshAgentsAsync(Func<CancellationToken, Task<RemoteFetch>> fetch) {
        lock (_lock) {
            if (_agentsBusy) { _agentsRerun = true; return; }
            _agentsBusy = true;
        }
        while (true) {
            int generation, laneEpoch;
            lock (_lock) { generation = _generation; laneEpoch = _lastLaneEpoch; }
            RemoteFetch? result = null;
            try {
                result = await fetch(CancellationToken.None).ConfigureAwait(false);
            } catch (Exception) {
                // Data-plane refresh: a throw here is a missed refresh, never an app fault.
            }
            if (result?.Rows is { Length: > 0 }) {
                Task? daemonsInFlight;
                lock (_lock) daemonsInFlight = _daemonsInFlight?.Task;
                if (daemonsInFlight is not null)
                    await daemonsInFlight.WaitAsync(DaemonsRefreshTimeout, _time, CancellationToken.None)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            var notifyUnauthorized = false;
            bool rerun;
            lock (_lock) {
                // A completion from before the latest connect must never repopulate the cache or
                // report an auth failure a fresher connect may already have superseded — checked
                // under the same lock a connect's generation bump takes.
                if (generation == _generation) {
                    if (result?.Rows is { } rows)
                        _agents.EditDiff(rows, EqualityComparer<AgentInstanceDto>.Default);
                    if (result is { Unauthorized: true }) notifyUnauthorized = true;
                }
                rerun = _agentsRerun;
                if (rerun) _agentsRerun = false;
                else _agentsBusy = false;
            }
            // Invoked outside this lock (arbitrary caller code — ServerConnectionService.
            // ParkSignedOut in production), carrying the lane epoch this fetch started under so
            // the lane itself re-validates the park at the moment it actually runs, rather than
            // trusting a decision this method already committed to before releasing its own lock.
            if (notifyUnauthorized) _onUnauthorized?.Invoke(laneEpoch);
            if (!rerun) return;
        }
    }

    public async Task RefreshDaemonsAsync() {
        lock (_lock) {
            if (_daemonsBusy) { _daemonsRerun = true; return; }
            _daemonsBusy = true;
            _daemonsInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        while (true) {
            int generation, listingGeneration;
            lock (_lock) { generation = _generation; listingGeneration = _listingGeneration; }
            IReadOnlyList<DaemonInfo>? result = null;
            using var cts = new CancellationTokenSource(DaemonsRefreshTimeout, _time);
            try {
                result = await _lane.GetConnectedDaemonsAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            } catch (Exception) {
            }
            TaskCompletionSource? completed;
            lock (_lock) {
                if (result is not null && generation == _generation)
                    _daemons.OnNext(result);
                if (listingGeneration == _listingGeneration) {
                    if (result is not null) {
                        ResetListingRetry();
                        _listing.OnNext(new(result, listingGeneration, false));
                    } else {
                        _listing.OnNext(new(null, listingGeneration, true));
                        if (!_daemonsRerun) ScheduleListingRetry();
                    }
                }
                if (_daemonsRerun) { _daemonsRerun = false; continue; }
                _daemonsBusy = false;
                completed = _daemonsInFlight;
                _daemonsInFlight = null;
            }
            completed?.TrySetResult();
            return;
        }
    }

    // The three below run under _lock. Cancelling bumps the ticket, so a timer callback that lost
    // the race with its own cancellation finds a newer ticket and does nothing.
    void ScheduleListingRetry() {
        CancelListingRetry();
        if (_disposed || !_laneConnected) return;
        var delay = ListingRetryDelays[Math.Min(_listingRetryAttempt++, ListingRetryDelays.Length - 1)];
        var ticket = _listingRetryTicket;
        _listingRetry = _time.CreateTimer(_ => RetryListing(ticket), null, delay, Timeout.InfiniteTimeSpan);
    }

    void ResetListingRetry() {
        _listingRetryAttempt = 0;
        CancelListingRetry();
    }

    void CancelListingRetry() {
        _listingRetryTicket++;
        _listingRetry?.Dispose();
        _listingRetry = null;
    }

    void RetryListing(long ticket) {
        lock (_lock) {
            if (ticket != _listingRetryTicket) return;
            CancelListingRetry();
        }
        _ = RefreshDaemonsAsync();
    }

    async Task RefreshDaemonAccountsAsync() {
        lock (_lock) {
            if (_daemonAccountsBusy) { _daemonAccountsRerun = true; return; }
            _daemonAccountsBusy = true;
        }
        while (true) {
            int generation;
            lock (_lock) generation = _generation;
            IReadOnlyList<DaemonAccountsDto>? result = null;
            using var cts = new CancellationTokenSource(DaemonsRefreshTimeout, _time);
            try {
                result = await _lane.GetDaemonAccountsAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            } catch (Exception) {
            }
            lock (_lock) {
                if (result is not null && generation == _generation)
                    _daemonAccounts.OnNext(result);
                if (_daemonAccountsRerun) { _daemonAccountsRerun = false; continue; }
                _daemonAccountsBusy = false;
            }
            return;
        }
    }

    /// Production fetch: authenticated GET {server}/api/agent-instances via the
    /// authenticated lane, client leased per call. Unreachable/non-auth failures
    /// come back as an empty (non-Unauthorized) RemoteFetch — lane loss is data, not a sign-in
    /// problem.
    public static Func<CancellationToken, Task<RemoteFetch>> HttpFetch(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new RemoteFetch(null);
        try {
            // The waiting verb: the rail keeps polling across a token's lifetime, so this leg has to
            // recover from a 401 rather than read one as the sign-out it reports to its caller.
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired))
                    return new RemoteFetch(null, Unauthorized: true);
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.AgentInstances}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return new RemoteFetch(null, Unauthorized: true);
                if (!response.IsSuccessStatusCode) return new RemoteFetch(null);
                var rows = await response.Content.ReadFromJsonAsync(
                    RemoteModelsJsonContext.Default.AgentInstanceDtoArray, ct).ConfigureAwait(false);
                return new RemoteFetch(rows);
            }
        } catch (Exception) {
            return new RemoteFetch(null);
        }
    };

    public void Dispose() {
        _subscriptions.Dispose();
        TaskCompletionSource? inFlight;
        lock (_lock) {
            _disposed = true;
            ResetListingRetry();
            inFlight = _daemonsInFlight;
            _daemonsInFlight = null;
        }
        inFlight?.TrySetResult();
        _agents.Dispose();
        _daemons.Dispose();
        _listing.Dispose();
        _daemonAccounts.Dispose();
    }
}
