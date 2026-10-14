using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Harness.Muse;

/// <summary>
/// The parent watcher's bookkeeping for the child watchers it spawned for Muse sessions: which
/// unannounced children are still held back, and which finished workers to stop. Pure given the
/// clock, so the policy is testable without a watcher.
/// </summary>
internal sealed class MuseChildWatchers {
    /// <summary>Scans an unannounced child is held back, so a late-flushed announcement still labels it.</summary>
    internal const int AnnouncementGraceScans = 3;

    /// <summary>A spawned child needs this long to start, register its SIGTERM handler, connect and make
    /// its first drain; a stop sooner than that kills it with its content unread.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    sealed record Child(string Log, DateTimeOffset SpawnedAt);

    readonly Dictionary<string, Child>    _spawned   = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>      _handled   = new(StringComparer.Ordinal);
    readonly HashSet<string>              _stopped   = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>      _held      = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _logMtimes = new(StringComparer.Ordinal);

    bool _recheck;
    int  _unconfirmedStops;

    /// <summary>Set when a result may have arrived or a child was spawned; cleared once no spawned child
    /// is left that is finished, not yet stopped and still inside the grace, and every selected stop
    /// has been confirmed.</summary>
    internal bool RecheckPending {
        get => _recheck || _unconfirmedStops > 0;
        set => _recheck = value;
    }

    internal bool ShouldHold(string childLog, bool announced) {
        if (announced) return false;

        var held = _held.GetValueOrDefault(childLog);
        if (held >= AnnouncementGraceScans) return false;

        _held[childLog] = held + 1;
        return true;
    }

    internal void Spawned(string key, string log, DateTimeOffset now) {
        _spawned[key]  = new Child(log, now);
        RecheckPending = true;
    }

    internal string LogOf(string key) => _spawned[key].Log;

    /// <summary>Spawned children to stop now: a result the parent reported since the child was last
    /// stopped or respawned, and a spawn older than <see cref="StopGrace"/>. Nothing counts as stopped
    /// until <see cref="Stopped"/> confirms it, so a stop that fails is selected again.</summary>
    internal IReadOnlyList<string> SelectStops(IReadOnlyDictionary<string, int> readyCounts, DateTimeOffset now) {
        var stops   = new List<string>();
        var waiting = false;

        foreach (var (key, child) in _spawned) {
            var ready = readyCounts.GetValueOrDefault(key);
            if (ready <= _handled.GetValueOrDefault(key)) continue;

            if (now - child.SpawnedAt >= StopGrace) {
                stops.Add(key);
            } else {
                waiting = true;
            }
        }

        _recheck          = waiting;
        _unconfirmedStops = stops.Count;
        return stops;
    }

    /// <summary>Confirms that the children <see cref="SelectStops"/> returned for these counts were stopped.</summary>
    internal void Stopped(IReadOnlyList<string> stops, IReadOnlyDictionary<string, int> readyCounts) {
        foreach (var key in stops) {
            _handled[key] = readyCounts[key];
            _stopped.Add(key);
        }

        _unconfirmedStops = 0;
    }

    internal void RecordLogMtime(string key, DateTime mtime) => WatchCommand.ChildRolloutAdvanced(_logMtimes, _spawned[key].Log, mtime);

    /// <summary>Stopped children whose log has grown since they were stopped: the worker took more work.
    /// They count as freshly spawned, and stop again only on a new result.</summary>
    internal IReadOnlyList<string> Reengaged(Func<string, DateTime> mtimeOf, DateTimeOffset now) {
        List<string>? back = null;

        foreach (var key in _stopped.ToArray()) {
            var log = _spawned[key].Log;
            if (!WatchCommand.ChildRolloutAdvanced(_logMtimes, log, mtimeOf(log))) continue;

            _stopped.Remove(key);
            _spawned[key] = new Child(log, now);
            (back ??= []).Add(key);
        }

        return back ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
