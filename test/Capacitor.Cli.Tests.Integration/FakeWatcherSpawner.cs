namespace Capacitor.Cli.Tests.Integration;

/// <summary>Records what the manager decided to launch, in place of launching it.</summary>
sealed class FakeWatcherSpawner(Func<WatcherSpawnRequest, Task> behaviour) : IWatcherSpawner {
    public FakeWatcherSpawner() : this(_ => Task.CompletedTask) { }

    public List<WatcherSpawnRequest> Spawned { get; } = [];

    public IEnumerable<string> Keys => Spawned.Select(request => request.Key);

    public Task SpawnAsync(WatcherSpawnRequest request) {
        Spawned.Add(request);

        return behaviour(request);
    }
}
