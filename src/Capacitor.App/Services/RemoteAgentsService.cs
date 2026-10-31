using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IRemoteAgentsService {
    /// Keyed by AgentId. Retained across lane loss — staleness is presentation.
    IObservableCache<AgentInstanceDto, string> Agents { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; }
    /// Replay-1, seeded unanswered at generation 0. Published after DrawableDaemonAccounts has taken
    /// the listing in, so an observer of a listing never reads a drawable gated on the one before.
    IObservable<DaemonListing> Listing { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts { get; }
    /// Replay-1, seeded with an empty list. The DaemonAccounts rows whose reading may be drawn: the lane
    /// is Connected, the daemon is in the current listing, and its accounts were fetched successfully by
    /// a fetch that started after its current incarnation was first seen.
    IObservable<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccounts { get; }
    /// Replay-1. True once a fetch in the lane's current connection returned rows; false while the
    /// lane is not Connected and after a fetch that returned none, when Agents is retained history.
    IObservable<bool> Current { get; }
    /// Fetches the daemon listing and every daemon's accounts now, ahead of any retry the backoff is
    /// waiting on.
    Task RefreshDaemonsAsync();
    /// Renames the session as the signed-in user; an applied rename shows in Agents at once.
    Task<SessionRenameResult> RenameAsync(string agentId, string sessionId, string title, CancellationToken ct);
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
    readonly BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> _drawableDaemonAccounts = new([]);
    readonly BehaviorSubject<bool> _current = new(false);
    readonly IDisposable _subscriptions;
    static readonly TimeSpan DaemonsRefreshTimeout = TimeSpan.FromSeconds(5);
    // The listing and the accounts otherwise refresh only on a connect or a push, so one failed fetch
    // on a healthy connection would leave account support unknown, or a remote target's accounts
    // missing, until the next.
    static readonly TimeSpan[] RetryDelays = [
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
    bool _connected;
    bool _disposed;
    bool _agentsBusy;
    bool _agentsRerun;
    bool _daemonsBusy;
    bool _daemonsRerun;
    bool _daemonAccountsBusy;
    bool _daemonAccountsRerun;
    TaskCompletionSource? _daemonsInFlight;
    // The drawable gate. A fetch that starts takes the next _accountsFetchSeq; a daemon's gate opens
    // only on a successful fetch whose seq is above the one current when its incarnation was seen,
    // so a fetch already in flight at that moment cannot vouch for it.
    int _accountsFetchSeq;
    bool _listingAnswered;
    readonly Dictionary<string, DaemonGate> _gates = new(StringComparer.Ordinal);
    readonly RetryBackoff _listingRetry;
    readonly RetryBackoff _accountsRetry;
    readonly IServerLane _lane;
    readonly Action<int>? _onUnauthorized;
    readonly TimeProvider _time;
    readonly Func<string, string, CancellationToken, Task<SessionRenameResult>>? _postRename;
    readonly SessionTitleOverlay _overlay;
    // Advances only when the signed-in subject changes, unlike _generation, which a dropped connection also advances.
    long _identity;

    public RemoteAgentsService(
            IServerLane lane, Func<CancellationToken, Task<RemoteFetch>> fetchAgents,
            TimeSpan? debounce = null, Action<int>? onUnauthorized = null, TimeProvider? time = null,
            Func<string, string, CancellationToken, Task<SessionRenameResult>>? postRename = null) {
        _lane = lane;
        _onUnauthorized = onUnauthorized;
        _postRename = postRename;
        _time = time ?? TimeProvider.System;
        _overlay = new(_time);
        _listingRetry = new(_time, RetryDelays);
        _accountsRetry = new(_time, RetryDelays);
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
        // place, and the listing reads unanswered before the new generation's fetch starts. Caches
        // clear only when the token subject changes; every Connected carrying a subject advances the
        // generation, and so does a lost connection, so an older fetch can neither publish nor report
        // an auth failure nor vouch for a lane that is gone.
        var laneStatus = lane.Status.Subscribe(s => {
            lock (_lock) {
                _connected = s.State == ServerLaneState.Connected;
                _listingRetry.Reset();
                _accountsRetry.Reset();
                _gates.Clear();
                _listingAnswered = false;
                if (!_connected) {
                    _generation++;
                    _current.OnNext(false);
                    PublishDrawable();
                    return;
                }
                if (s.Subject is not null) {
                    if (_lastSubject is not null && _lastSubject != s.Subject) {
                        _agents.Clear();
                        _overlay.Clear();
                        _identity++;
                        _daemons.OnNext([]);
                        _daemonAccounts.OnNext([]);
                    }
                    _generation++;
                    _lastSubject = s.Subject;
                }
                _listingGeneration++;
                _lastLaneEpoch = s.Epoch;
                PublishDrawable();
                _listing.OnNext(new(null, _listingGeneration, false));
            }
        });

        // Merge, not Concat: triggers must reach the refresh methods concurrently so a busy
        // refresh's admission check can actually observe contention and coalesce. Daemons
        // subscribes before Agents so a Connected edge starts the daemon refresh first and
        // RefreshAgentsAsync can await its completion before publishing rows that AgentDirectory
        // deduplicates against Daemons.
        var refreshDaemons = everyConnect.Merge(lane.DaemonsChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(() => RefreshListingAsync()))
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
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccounts => _drawableDaemonAccounts.AsObservable();
    public IObservable<bool> Current => _current.AsObservable();

    async Task RefreshAgentsAsync(Func<CancellationToken, Task<RemoteFetch>> fetch) {
        lock (_lock) {
            if (_agentsBusy) { _agentsRerun = true; return; }
            _agentsBusy = true;
        }
        while (true) {
            int generation, laneEpoch;
            long seq;
            lock (_lock) { generation = _generation; laneEpoch = _lastLaneEpoch; seq = _overlay.BeginRead(); }
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
                // A refresh admitted after the lane dropped is as stale as one that outlived it.
                if (generation == _generation && _connected) {
                    if (result?.Rows is { } rows)
                        _agents.EditDiff(_overlay.Resolve(rows, r => r.SessionId, r => r.Title, (r, title) => r with { Title = title }, seq),
                            EqualityComparer<AgentInstanceDto>.Default);
                    if (result is { Unauthorized: true }) notifyUnauthorized = true;
                    _current.OnNext(result?.Rows is not null);
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

    public async Task<SessionRenameResult> RenameAsync(string agentId, string sessionId, string title, CancellationToken ct) {
        if (_postRename is null) return new SessionRenameResult(false, null, SessionRenameReasons.Unauthenticated);
        long identity;
        lock (_lock) identity = _identity;
        var result = await _postRename(sessionId, title, ct).ConfigureAwait(false);
        if (!result.Applied) return result;
        lock (_lock) {
            if (identity != _identity)
                return new SessionRenameResult(false, null, SessionRenameReasons.IdentityMismatch);
            if (SessionTitleOverlay.Key(sessionId) is not { } key) return result;
            var shown = result.Title is { Length: > 0 } ? result.Title : RenameWire.NormalizeTitle(title) ?? title;
            var cached = _agents.Items.Where(a => SessionTitleOverlay.Key(a.SessionId) == key).ToList();
            _overlay.Record(sessionId, shown, cached.FirstOrDefault()?.Title);
            var affected = cached.Select(a => a with { Title = shown }).ToList();
            if (affected.Count > 0) _agents.AddOrUpdate(affected);
        }
        return result;
    }

    public Task RefreshDaemonsAsync() => Task.WhenAll(RefreshListingAsync(), RefreshDaemonAccountsAsync());

    async Task RefreshListingAsync() {
        lock (_lock) {
            if (_disposed) return;
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
            TaskCompletionSource? completed = null;
            bool rerun, newIncarnation = false;
            lock (_lock) {
                // Dispose takes this lock before disposing the subjects and has already released the in-flight waiter.
                if (_disposed) { _daemonsBusy = false; return; }
                if (result is not null && generation == _generation)
                    _daemons.OnNext(result);
                if (listingGeneration == _listingGeneration) {
                    if (result is not null) {
                        _listingRetry.Reset();
                        newIncarnation = TrackIncarnations(result);
                        _listingAnswered = true;
                    } else {
                        _listingAnswered = false;
                        if (!_daemonsRerun) ScheduleRetry(_listingRetry, RefreshListingAsync);
                    }
                    PublishDrawable();
                    _listing.OnNext(new(result, listingGeneration, result is null));
                }
                rerun = _daemonsRerun;
                if (rerun) _daemonsRerun = false;
                else {
                    _daemonsBusy = false;
                    completed = _daemonsInFlight;
                    _daemonsInFlight = null;
                }
            }
            // Accounts otherwise refresh only on a connect or a push, neither of which has to follow
            // this listing, so a new incarnation would stay closed with nothing left to open it.
            if (newIncarnation) _ = RefreshDaemonAccountsAsync();
            if (rerun) continue;
            completed?.TrySetResult();
            return;
        }
    }

    // Runs under _lock. True when any daemon's incarnation is new or changed.
    bool TrackIncarnations(IReadOnlyList<DaemonInfo> listing) {
        var incarnations = listing
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join('\n', g
                .Select(d => $"{d.OwnerUserId}\t{d.ConnectedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}\t{d.MachineId}")
                .Order(StringComparer.Ordinal)), StringComparer.Ordinal);
        foreach (var name in _gates.Keys.Where(n => !incarnations.ContainsKey(n)).ToList())
            _gates.Remove(name);
        var changed = false;
        foreach (var (name, incarnation) in incarnations) {
            if (_gates.TryGetValue(name, out var gate) && gate.Incarnation == incarnation) continue;
            _gates[name] = new(incarnation, _accountsFetchSeq, Fetched: false);
            changed = true;
        }
        return changed;
    }

    // Runs under _lock.
    void PublishDrawable() {
        if (_disposed) return;
        IReadOnlyList<DaemonAccountsDto> drawable = _connected && _listingAnswered
            ? [.. _daemonAccounts.Value.Where(d => _gates.TryGetValue(d.DaemonName, out var gate) && gate.Fetched)]
            : [];
        if (!drawable.SequenceEqual(_drawableDaemonAccounts.Value)) _drawableDaemonAccounts.OnNext(drawable);
    }

    readonly record struct DaemonGate(string Incarnation, int SeenAtSeq, bool Fetched);

    // Runs under _lock. Nothing is pending while the lane is down or after Dispose: both reset every backoff.
    void ScheduleRetry(RetryBackoff backoff, Func<Task> refresh) {
        if (_disposed || !_connected) return;
        backoff.Schedule(ticket => {
            lock (_lock) {
                if (!backoff.Claim(ticket)) return;
            }
            _ = refresh();
        });
    }

    async Task RefreshDaemonAccountsAsync() {
        lock (_lock) {
            if (_disposed) return;
            if (_daemonAccountsBusy) { _daemonAccountsRerun = true; return; }
            _daemonAccountsBusy = true;
        }
        while (true) {
            int generation, seq;
            lock (_lock) { generation = _generation; seq = ++_accountsFetchSeq; }
            IReadOnlyList<DaemonAccountsDto>? result = null;
            using var cts = new CancellationTokenSource(DaemonsRefreshTimeout, _time);
            try {
                result = await _lane.GetDaemonAccountsAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            } catch (Exception) {
            }
            lock (_lock) {
                // Dispose takes this lock before disposing the subject, so a result landing after it stops here.
                if (_disposed) { _daemonAccountsBusy = false; return; }
                if (result is not null) {
                    _accountsRetry.Reset();
                    if (generation == _generation) {
                        _daemonAccounts.OnNext(result);
                        foreach (var (name, gate) in _gates.Where(g => g.Value.SeenAtSeq < seq).ToList())
                            _gates[name] = gate with { Fetched = true };
                        PublishDrawable();
                    }
                } else if (!_daemonAccountsRerun) {
                    ScheduleRetry(_accountsRetry, RefreshDaemonAccountsAsync);
                }
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

    /// Production rename: authenticated POST {server}/hooks/agent-title with rename=true. Every
    /// failure is a reason; only the caller's own cancellation throws.
    public static Func<string, string, CancellationToken, Task<SessionRenameResult>> HttpRename(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, title, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl))
            return new SessionRenameResult(false, null, SessionRenameReasons.Unauthenticated);
        if (RenameWire.NormalizeTitle(title) is not { } normalized)
            return new SessionRenameResult(false, null, SessionRenameReasons.Malformed);
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } canonical)
            return new SessionRenameResult(false, null, SessionRenameReasons.NotFound);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired))
                    return new SessionRenameResult(false, null, SessionRenameReasons.Unauthenticated);
                var payload = new JsonObject {
                    ["session_id"] = canonical,
                    ["title"]      = normalized,
                    ["rename"]     = true,
                };
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"{serverUrl.TrimEnd('/')}/hooks/agent-title", content, ct).ConfigureAwait(false);
                switch (response.StatusCode) {
                    case HttpStatusCode.Forbidden:    return new SessionRenameResult(false, null, SessionRenameReasons.NotOwned);
                    case HttpStatusCode.NotFound:     return new SessionRenameResult(false, null, SessionRenameReasons.NotFound);
                    case HttpStatusCode.Unauthorized: return new SessionRenameResult(false, null, SessionRenameReasons.Unauthenticated);
                }
                if (!response.IsSuccessStatusCode) return new SessionRenameResult(false, null, SessionRenameReasons.ServerError);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var shown = doc.RootElement.Str("title");
                return doc.RootElement.Bool("applied") switch {
                    true  => new SessionRenameResult(true, shown, null),
                    false => new SessionRenameResult(false, shown, SessionRenameReasons.Refused),
                    null  => new SessionRenameResult(false, null, SessionRenameReasons.ServerError),
                };
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return new SessionRenameResult(false, null, SessionRenameReasons.ServerError);
        }
    };

    public void Dispose() {
        _subscriptions.Dispose();
        TaskCompletionSource? inFlight;
        lock (_lock) {
            _disposed = true;
            _listingRetry.Reset();
            _accountsRetry.Reset();
            inFlight = _daemonsInFlight;
            _daemonsInFlight = null;
        }
        inFlight?.TrySetResult();
        _agents.Dispose();
        _daemons.Dispose();
        _listing.Dispose();
        _daemonAccounts.Dispose();
        _drawableDaemonAccounts.Dispose();
        _current.Dispose();
    }
}
