namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// The deadline on a Pi reviewer's rounds. A prompt is written on a caller's thread while the read pump
/// handles frames on another, so the state lives under one lock and never consults a caller's view of
/// "busy", which can be stale in either direction.
///
/// <para>Invariant, re-established at every transition: the deadline is armed exactly when a written
/// prompt is unanswered, an accepted prompt has not started, or Pi is running.</para>
/// </summary>
internal sealed class PiRoundCeiling(TimeSpan limit, TimeProvider time, Action onExpired) : IDisposable {
    readonly Lock            _lock     = new();
    readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    int     _owed;
    bool    _running;
    bool    _terminal;
    long    _generation;
    ITimer? _timer;

    internal bool IsArmed { get { lock (_lock) return _timer is not null; } }

    internal bool InvariantHolds {
        get { lock (_lock) return (_timer is not null) == WorkOutstanding; }
    }

    bool WorkOutstanding => !_terminal && (_inFlight.Count > 0 || _owed > 0 || _running);

    /// <summary>Call before the write, so no frame can arrive between the write and the arming.</summary>
    internal void PromptWriting(string id) {
        lock (_lock) {
            if (_terminal) return;
            _inFlight.Add(id);
            if (_timer is null) Arm();
        }
    }

    internal void PromptWriteFailed(string id) {
        lock (_lock) {
            _inFlight.Remove(id);
            Reconcile();
        }
    }

    internal void Response(string id, bool accepted) {
        lock (_lock) {
            if (!_inFlight.Remove(id)) return;
            if (accepted) _owed++;
            Reconcile();
        }
    }

    internal void AgentStarted() {
        lock (_lock) {
            if (_terminal) return;
            _running = true;
            if (_timer is null) Arm();
        }
    }

    /// <summary>A round is starting: it gets its own full budget, whatever was left of the last one's.</summary>
    internal void UserEcho() {
        lock (_lock) {
            if (_terminal) return;
            if (_owed > 0) _owed--;
            _running = true;
            Arm();
        }
    }

    /// <summary>Pi emits this only when its queue is empty. Work it has not started yet is still owed,
    /// and must not inherit a nearly spent deadline.</summary>
    internal void AgentSettled() {
        lock (_lock) {
            if (_terminal) return;
            _running = false;
            if (WorkOutstanding) Arm();
            else Disarm();
        }
    }

    /// <summary>A child that ends on its own is an ordinary death. After this, no timeout can fire.</summary>
    internal void Terminal() {
        lock (_lock) {
            _terminal = true;
            _inFlight.Clear();
            _owed    = 0;
            _running = false;
            Disarm();
        }
    }

    public void Dispose() => Terminal();

    void Reconcile() {
        if (WorkOutstanding) { if (_timer is null) Arm(); }
        else Disarm();
    }

    void Arm() {
        _timer?.Dispose();
        var generation = ++_generation;
        _timer = time.CreateTimer(_ => Expire(generation), null, limit, Timeout.InfiniteTimeSpan);
    }

    void Disarm() {
        _timer?.Dispose();
        _timer = null;
        _generation++;
    }

    void Expire(long generation) {
        lock (_lock) {
            if (_terminal || generation != _generation || !WorkOutstanding) return;
            Disarm();
        }

        // Outside the lock: the callback claims the reap, which takes the gate's own lock.
        onExpired();
    }
}
