namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Reached at each <see cref="SnapshotPoint"/>. Production passes straight through at all of them.
/// The claim protocol's correctness is only observable while two callers genuinely overlap, and a
/// wall-clock race for that window would be flaky and could pass by luck — so a caller is held here
/// instead, and failed here rather than at whatever moment an induced I/O error happens to land.
/// </summary>
public interface ISnapshotBarrier {
    Task ReachedAsync(SnapshotPoint point);
}
