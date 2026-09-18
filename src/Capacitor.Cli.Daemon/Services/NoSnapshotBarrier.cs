namespace Capacitor.Cli.Daemon.Services;

/// <summary>Passes straight through at every point.</summary>
public sealed class NoSnapshotBarrier : ISnapshotBarrier {
    public static readonly NoSnapshotBarrier Instance = new();

    public Task ReachedAsync(SnapshotPoint point) => Task.CompletedTask;
}
