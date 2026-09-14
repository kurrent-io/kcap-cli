using System.Collections.Concurrent;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Holds or fails a snapshot caller at chosen points. Armed and disarmed during a test, because the
/// same manager serves both callers and the second must not be parked where the first was.
/// </summary>
sealed class FakeSnapshotBarrier : ISnapshotBarrier {
    readonly ConcurrentDictionary<SnapshotPoint, Func<Task>> _reached = new();

    public void At(SnapshotPoint point, Func<Task> reached) => _reached[point] = reached;

    public void FailAt(SnapshotPoint point) =>
        At(point, () => throw new InvalidOperationException("injected_standalone_failure"));

    public void Clear(SnapshotPoint point) => _reached.TryRemove(point, out _);

    public Task ReachedAsync(SnapshotPoint point) =>
        _reached.TryGetValue(point, out var reached) ? reached() : Task.CompletedTask;
}
