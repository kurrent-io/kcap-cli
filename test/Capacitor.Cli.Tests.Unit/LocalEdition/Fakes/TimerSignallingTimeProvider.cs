using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>A fake clock that says when a timer is registered, so a test can advance it exactly past a delay the code
/// under test has already started, instead of racing it.</summary>
sealed class TimerSignallingTimeProvider : FakeTimeProvider {
    /// <summary>Runs with each new timer's due time, after the timer is registered.</summary>
    public Action<TimeSpan>? OnTimerCreated { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        OnTimerCreated?.Invoke(dueTime);

        return timer;
    }
}
