using System.Threading.Channels;
using Capacitor.Cli.Core.Accounts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Refreshes every registered account once at start, then whenever the shared registry says it is due,
/// then on request, one probe at a time. A request inside an account's cooldown waits for its end as one
/// deferred refresh, never dropped, so a limit hit just after a refresh is still looked at. Requests for an
/// account already queued, deferred or running coalesce. Periodic checks stay out of that bookkeeping: one
/// stands down for an account a request will refresh anyway, and one that finds it not due counts as no
/// refresh at all.</summary>
internal sealed partial class AccountRefresher(
        DaemonConfig config, Func<VendorAccount, IAccountProbe> probeFor, TimeProvider time, ILogger<AccountRefresher> logger)
    : BackgroundService {
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Tick     = TimeSpan.FromMinutes(2);

    readonly Channel<(string Id, bool Periodic)> _work =
        Channel.CreateUnbounded<(string, bool)>(new UnboundedChannelOptions { SingleReader = true });

    readonly Lock                               _lock           = new();
    readonly HashSet<string>                    _queued         = new(StringComparer.Ordinal);
    readonly Dictionary<string, ITimer?>        _deferred       = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _lastFinished   = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _lastSoon       = new(StringComparer.Ordinal);
    readonly HashSet<string>                    _soonPending    = new(StringComparer.Ordinal);
    readonly HashSet<string>                    _periodicQueued = new(StringComparer.Ordinal);
    ITimer?                                     _tick;
    string?                                     _running;
    bool                                        _stopped;
    bool                                        _started;
    long                                        _finished;

    readonly DaemonStatusNotifier _progress = new();

    internal bool Periodic { get; init; } = true;

    /// <summary>Test seam: awaited once a periodic check is running, before it looks at the registry.</summary>
    internal Func<string, CancellationToken, Task>? BeforePeriodicCheck { get; set; }

    /// <summary>Refreshes completed, failed ones included.</summary>
    internal long Finished => Interlocked.Read(ref _finished);

    /// <summary>The startup requests are in and nothing is queued or running; a deferred refresh waits on time.</summary>
    internal bool IsIdle {
        get { lock (_lock) return _started && _queued.Count == 0 && _periodicQueued.Count == 0 && _running is null; }
    }

    /// <summary>Test seam: completes once <paramref name="condition"/> holds, re-checked after every queue,
    /// start and finish.</summary>
    internal async Task WaitForAsync(Func<bool> condition, CancellationToken ct) {
        while (true) {
            var seen = _progress.Version;
            if (condition()) return;
            await _progress.WaitBeyondAsync(seen, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Never blocks and never throws: it is called under the publisher's rebuild gate.</summary>
    public void Request(string accountId) {
        lock (_lock) RequestHeld(accountId);
    }

    void RequestHeld(string accountId) {
        if (_stopped || _queued.Contains(accountId) || _deferred.ContainsKey(accountId)) return;

        if (_running == accountId) {
            // Scheduled once this run finishes and its cooldown starts.
            _deferred[accountId] = null;
            return;
        }

        var now = time.GetUtcNow();
        if (_lastFinished.TryGetValue(accountId, out var last) && now - last < Cooldown) {
            Defer(accountId, last + Cooldown - now);
            return;
        }

        EnqueueHeld(accountId);
    }

    /// <summary>Like <see cref="Request"/>, but skips the cooldown once per cooldown, so an edit to an account's own
    /// files is looked at within a probe. A probe that itself edits those files still cannot run more often than the
    /// cooldown. Never blocks and never throws.</summary>
    public void RequestSoon(string accountId) {
        lock (_lock) RequestSoonHeld(accountId);
    }

    void RequestSoonHeld(string accountId) {
        var now = time.GetUtcNow();
        var due = !_stopped && !_queued.Contains(accountId) && _running != accountId
               && !(_lastSoon.TryGetValue(accountId, out var last) && now - last < Cooldown);
        // Whichever probe serves this request spends the budget when it starts.
        if (!_stopped) _soonPending.Add(accountId);
        if (due) {
            if (_deferred.Remove(accountId, out var timer)) timer?.Dispose();
            EnqueueHeld(accountId);
            return;
        }

        RequestHeld(accountId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.Yield();
        if (config.Accounts is not { } store) return;

        try {
            foreach (var account in store.Load().Accounts) Request(account.Id);
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            LogRegistryUnreadable(logger, ex);
        }

        lock (_lock) {
            _started = true;
            if (Periodic && !_stopped) _tick = time.CreateTimer(_ => OnTick(store), null, Tick, Tick);
        }
        _progress.Pulse();

        try {
            await foreach (var (id, periodic) in _work.Reader.ReadAllAsync(stoppingToken)) {
                if (periodic) {
                    await CheckAsync(store, id, stoppingToken);
                    continue;
                }

                lock (_lock) {
                    _queued.Remove(id);
                    _running = id;
                    if (_soonPending.Remove(id)) _lastSoon[id] = time.GetUtcNow();
                }
                _progress.Pulse();

                try {
                    await RefreshAsync(store, id, null, stoppingToken);
                } finally {
                    lock (_lock) {
                        _running          = null;
                        _lastFinished[id] = time.GetUtcNow();
                        if (_deferred.TryGetValue(id, out var timer) && timer is null) Defer(id, Cooldown);
                        Interlocked.Increment(ref _finished);
                    }
                    _progress.Pulse();
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutdown */ }
    }

    /// <summary>Queues a check of every registered account no request already covers. Never throws: it runs on
    /// a timer.</summary>
    void OnTick(AccountStore store) {
        IReadOnlyList<VendorAccount> accounts;
        try {
            accounts = store.Load().Accounts;
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            LogTickRegistryUnreadable(logger, ex);
            return;
        }

        lock (_lock) {
            if (_stopped) return;

            foreach (var id in accounts.Select(a => a.Id)) {
                if (_queued.Contains(id) || _deferred.ContainsKey(id) || _running == id || !_periodicQueued.Add(id)) continue;

                _work.Writer.TryWrite((id, true));
                _progress.Pulse();
            }
        }
    }

    async Task CheckAsync(AccountStore store, string id, CancellationToken ct) {
        bool skipped;
        lock (_lock) {
            _periodicQueued.Remove(id);
            skipped = _queued.Contains(id) || _deferred.ContainsKey(id);
            if (!skipped) _running = id;
        }
        _progress.Pulse();
        if (skipped) return;

        var probed = true;
        try {
            if (BeforePeriodicCheck is { } before) await before(id, ct);
            probed = await RefreshAsync(store, id, Interval, ct);
        } finally {
            lock (_lock) {
                _running = null;
                if (probed) {
                    _lastFinished[id] = time.GetUtcNow();
                    if (_deferred.TryGetValue(id, out var timer) && timer is null) Defer(id, Cooldown);
                    Interlocked.Increment(ref _finished);
                } else if (_deferred.TryGetValue(id, out var timer) && timer is null) {
                    // Nothing was refreshed, so a request that arrived meanwhile is judged against the last real refresh.
                    _deferred.Remove(id);
                    if (_soonPending.Contains(id)) RequestSoonHeld(id);
                    else RequestHeld(id);
                }
            }
            _progress.Pulse();
        }
    }

    /// <returns>False only when <paramref name="dueAfter"/> is set and the account was not due.</returns>
    async Task<bool> RefreshAsync(AccountStore store, string accountId, TimeSpan? dueAfter, CancellationToken ct) {
        try {
            if (store.Load().Accounts.FirstOrDefault(a => a.Id == accountId) is not { } account) return true;

            var probe   = probeFor(account);
            var outcome = dueAfter is { } interval
                ? await AccountRefresh.RunIfDueAsync(store, accountId, probe, time, interval, ct)
                : await AccountRefresh.RunAsync(store, accountId, probe, time, ct);
            if (outcome is null) return false;

            foreach (var step in outcome.Steps.Where(s => !s.Succeeded)) LogStepFailed(logger, accountId, step.Name, step.Detail);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            LogRefreshFailed(logger, accountId, ex);
        }

        return true;
    }

    void Defer(string accountId, TimeSpan due) {
        if (due <= TimeSpan.Zero) {
            _deferred.Remove(accountId);
            EnqueueHeld(accountId);
            return;
        }

        _deferred.GetValueOrDefault(accountId)?.Dispose();
        _deferred[accountId] = time.CreateTimer(_ => {
            lock (_lock) {
                if (_stopped || !_deferred.Remove(accountId, out var self)) return;

                self?.Dispose();
                EnqueueHeld(accountId);
            }
        }, null, due, Timeout.InfiniteTimeSpan);
    }

    void EnqueueHeld(string accountId) {
        if (!_queued.Add(accountId)) return;

        _work.Writer.TryWrite((accountId, false));
        _progress.Pulse();
    }

    public override void Dispose() {
        lock (_lock) {
            _stopped = true;
            _tick?.Dispose();
            foreach (var timer in _deferred.Values) timer?.Dispose();
            _deferred.Clear();
        }

        _work.Writer.TryComplete();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The account registry could not be read; skipping the startup refresh")]
    static partial void LogRegistryUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The account registry could not be read; skipping this periodic check")]
    static partial void LogTickRegistryUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing account {AccountId} failed")]
    static partial void LogRefreshFailed(ILogger logger, string accountId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refreshing account {AccountId}: {Step} failed: {Detail}")]
    static partial void LogStepFailed(ILogger logger, string accountId, string step, string detail);
}
