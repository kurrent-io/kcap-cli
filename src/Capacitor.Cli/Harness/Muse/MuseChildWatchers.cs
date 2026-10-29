using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Harness.Muse;

/// <summary>
/// The parent watcher's bookkeeping for the child watchers it spawned for Muse sessions: which
/// unannounced children are still held back, and which finished workers to stop. Pure given the
/// clock apart from <see cref="Stop"/>, so the policy is testable without a watcher.
/// </summary>
internal sealed class MuseChildWatchers {
    /// <summary>Scans an unannounced child is held back, so a late-flushed announcement still labels it.</summary>
    internal const int AnnouncementGraceScans = 3;

    /// <summary>A spawned child needs this long to start, register its SIGTERM handler, connect and make
    /// its first drain; a stop sooner than that kills it with its content unread. It also runs from the
    /// moment a result is first observed, so the child's last writes flush before it is stopped.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>First wait after a failed spawn: past the one-second scan so a persistent failure stops
    /// hammering the server, short enough that a transient one recovers within a few seconds.</summary>
    internal static readonly TimeSpan SpawnRetryInitial = TimeSpan.FromSeconds(5);

    /// <summary>Longest wait between spawn attempts, which bounds how long a recovered child goes unrecorded.</summary>
    internal static readonly TimeSpan SpawnRetryCap = TimeSpan.FromMinutes(1);

    sealed record Child(string Log, DateTimeOffset SpawnedAt);

    sealed record Observed(int Count, DateTimeOffset At);

    sealed record Backoff(int Failures, DateTimeOffset NextAttempt);

    readonly Dictionary<string, Child>    _spawned   = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>      _handled   = new(StringComparer.Ordinal);
    readonly HashSet<string>              _stopped   = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>      _held      = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _logMtimes = new(StringComparer.Ordinal);

    readonly HashSet<string>             _unconfirmed = new(StringComparer.Ordinal);
    readonly HashSet<string>             _registered  = new(StringComparer.Ordinal);
    readonly Dictionary<string, Observed> _observed = new(StringComparer.Ordinal);
    readonly HashSet<string>             _restartPending = new(StringComparer.Ordinal);
    readonly Dictionary<string, Backoff> _backoff     = new(StringComparer.Ordinal);

    bool _recheck;

    /// <summary>Set when a result may have arrived or a child was spawned; cleared once no spawned child
    /// is left that is finished, not yet stopped and still inside the grace, and every selected stop
    /// has been confirmed.</summary>
    internal bool RecheckPending {
        get => _recheck || _unconfirmed.Count > 0;
        set => _recheck = value;
    }

    internal bool ShouldHold(string childLog, bool announced) {
        if (announced) return false;

        var held = _held.GetValueOrDefault(childLog);
        if (held >= AnnouncementGraceScans) return false;

        _held[childLog] = held + 1;
        return true;
    }

    /// <summary>Whether the server has acknowledged this child's registration. It is posted once, however
    /// many times the spawn is retried.</summary>
    internal bool IsRegistered(string key) => _registered.Contains(key);

    internal void Registered(string key) => _registered.Add(key);

    /// <summary>Whether a spawn may be attempted now: always for a child that has not failed, otherwise
    /// only once its backoff has elapsed.</summary>
    internal bool ShouldAttemptSpawn(string key, DateTimeOffset now) =>
        !_backoff.TryGetValue(key, out var backoff) || now >= backoff.NextAttempt;

    /// <summary>Records a failed spawn and returns the wait before the next attempt: doubling from
    /// <see cref="SpawnRetryInitial"/>, capped at <see cref="SpawnRetryCap"/>.</summary>
    internal TimeSpan SpawnFailed(string key, DateTimeOffset now) {
        var failures = _backoff.TryGetValue(key, out var previous) ? previous.Failures + 1 : 1;
        var delay    = TimeSpan.FromTicks(Math.Min(SpawnRetryCap.Ticks, SpawnRetryInitial.Ticks << Math.Min(failures - 1, 16)));

        _backoff[key] = new Backoff(failures, now + delay);
        return delay;
    }

    /// <summary>Clears a child's backoff once its watcher is running.</summary>
    internal void SpawnSucceeded(string key) {
        _backoff.Remove(key);
        _restartPending.Remove(key);
    }

    internal void Spawned(string key, string log, DateTimeOffset now) {
        _spawned[key]  = new Child(log, now);
        _backoff.Remove(key);
        _restartPending.Remove(key);
        RecheckPending = true;
    }

    internal string LogOf(string key) => _spawned[key].Log;

    /// <summary>Spawned children to stop now: a result the parent reported since the child was last
    /// stopped or respawned, once <see cref="StopGrace"/> has passed since the later of its spawn and the
    /// first time that result count was observed. Nothing counts as stopped
    /// until <see cref="Stopped"/> confirms it, so a stop that fails is selected again.</summary>
    internal IReadOnlyList<string> SelectStops(IReadOnlyDictionary<string, int> readyCounts, DateTimeOffset now) {
        var stops   = new List<string>();
        var waiting = false;

        foreach (var (key, child) in _spawned) {
            var ready = readyCounts.GetValueOrDefault(key);
            if (ready <= _handled.GetValueOrDefault(key)) continue;

            if (!_observed.TryGetValue(key, out var seen) || seen.Count != ready) {
                seen          = new Observed(ready, now);
                _observed[key] = seen;
            }

            var since = seen.At > child.SpawnedAt ? seen.At : child.SpawnedAt;

            if (now - since >= StopGrace) {
                stops.Add(key);
            } else {
                waiting = true;
            }
        }

        _recheck = waiting;
        _unconfirmed.Clear();
        _unconfirmed.UnionWith(stops);
        return stops;
    }

    /// <summary>Confirms which of the children <see cref="SelectStops"/> returned for these counts were
    /// stopped. One left out stays selectable and keeps <see cref="RecheckPending"/> armed.</summary>
    internal void Stopped(IEnumerable<string> stopped, IReadOnlyDictionary<string, int> readyCounts) {
        foreach (var key in stopped) {
            _handled[key] = readyCounts[key];
            _stopped.Add(key);
            _unconfirmed.Remove(key);
        }
    }

    /// <summary>Stops the children <see cref="SelectStops"/> returned and confirms each one that has no
    /// process left running. Returns how many were confirmed.</summary>
    internal async Task<int> Stop(WatcherManager watchers, IReadOnlyList<string> stops, IReadOnlyDictionary<string, int> readyCounts) {
        var results = await Task.WhenAll(stops.Select(async key => (Key: key, Done: await watchers.TryStopWatcher(key))));
        var stopped = results.Where(r => r.Done).Select(r => r.Key).ToArray();
        Stopped(stopped, readyCounts);

        return stopped.Length;
    }

    internal void RecordLogMtime(string key, DateTime mtime) => WatchCommand.ChildRolloutAdvanced(_logMtimes, _spawned[key].Log, mtime);

    /// <summary>Stopped children whose log has grown since they were stopped: the worker took more work.
    /// They count as freshly spawned, and stop again only on a new result.</summary>
    internal IReadOnlyList<string> Reengaged(Func<string, DateTime> mtimeOf, DateTimeOffset now) {
        List<string>? back = null;

        foreach (var key in _stopped.ToArray()) {
            if (!ShouldAttemptSpawn(key, now)) continue;

            var log = _spawned[key].Log;
            var advanced = WatchCommand.ChildRolloutAdvanced(_logMtimes, log, mtimeOf(log));
            if (!advanced && !_restartPending.Contains(key)) continue;

            _stopped.Remove(key);
            _spawned[key] = new Child(log, now);
            (back ??= []).Add(key);
        }

        return back ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>Puts a child <see cref="Reengaged"/> returned back to stopped when its watcher did not
    /// start, and marks the restart pending so <see cref="Reengaged"/> returns it again once the returned
    /// backoff has elapsed, whatever the log's mtime does meanwhile. Only a confirmed start clears it.</summary>
    internal TimeSpan RestartFailed(string key, DateTimeOffset now) {
        _stopped.Add(key);
        _restartPending.Add(key);
        return SpawnFailed(key, now);
    }
}
