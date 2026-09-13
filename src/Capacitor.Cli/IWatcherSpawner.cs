namespace Capacitor.Cli;

/// <summary>
/// Launches one watcher process. Separate from the manager that decides whether to: the decision is
/// lock-guarded reap-and-respawn logic worth exercising on its own, and the launch is a real OS
/// process that would otherwise have to run for it.
/// </summary>
public interface IWatcherSpawner {
    Task SpawnAsync(WatcherSpawnRequest request);
}
