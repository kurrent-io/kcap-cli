using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// Forwards to a <see cref="FakeTimeProvider"/> and signals the first time a caller arms a delay on it,
/// so a test can change the world while a poll is parked and only then advance the clock.
sealed class DelayObservingTime(FakeTimeProvider inner, TaskCompletionSource parked) : TimeProvider {
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        // Register the timer before signalling. The waiter advances the clock from that signal,
        // and an advance that lands before the timer exists never fires.
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        parked.TrySetResult();
        return timer;
    }
}
