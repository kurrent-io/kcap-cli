using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>A fake clock that says when something first starts waiting on it. A lock wait is the only
/// thing in the scope commands that creates a timer, so the signal means the command is parked on a lock.</summary>
sealed class TimerSignallingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start) {
    readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Waiting => _waiting.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        _waiting.TrySetResult();

        return base.CreateTimer(callback, state, dueTime, period);
    }
}
