namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A clock whose timer never fires on its own: the test advances time and dispatches the callback
/// itself, so "the deadline has passed but the callback has not run yet" is a state it can hold for
/// as long as it likes. Wall clock and monotonic timestamp move together, as on a FakeTimeProvider.
sealed class ManualTimerTimeProvider : TimeProvider {
    long           _ticks;
    DateTimeOffset _utcNow = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// The subagent-expiry timer: the orchestrator also starts a periodic status-report timer on
    /// this provider, so only the rested-infinite creation shape unique to the expiry timer is
    /// recorded, never whichever timer was created last.
    public ManualTimer? Timer { get; private set; }

    public override long           TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long           GetTimestamp()     => _ticks;
    public override DateTimeOffset GetUtcNow()        => _utcNow;

    public void Advance(TimeSpan by) {
        _ticks  += by.Ticks;
        _utcNow += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        var timer = new ManualTimer(callback, state, dueTime);
        if (dueTime == Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan) Timer = timer;
        return timer;
    }

    public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer {
        /// The due time of the last Change; Timeout.InfiniteTimeSpan when rested.
        public TimeSpan Due      { get; private set; } = dueTime;
        public int      Changes  { get; private set; }
        public bool     Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) {
            Due = dueTime;
            Changes++;
            return true;
        }

        public void Fire() => callback(state);

        public void      Dispose()      => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
