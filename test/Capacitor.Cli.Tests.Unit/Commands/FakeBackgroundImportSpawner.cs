using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

sealed class FakeBackgroundImportSpawner(BackgroundImportLaunch result) : IBackgroundImportSpawner {
    public BackgroundImportRequest? Seen { get; private set; }
    public int Spawns { get; private set; }

    public static FakeBackgroundImportSpawner Running()   => new(new(BackgroundImportStatus.Running,    "/tmp/import-x.log", null, null));
    public static FakeBackgroundImportSpawner ExitedZero() => new(new(BackgroundImportStatus.ExitedZero, "/tmp/import-x.log", 0, null));
    public static FakeBackgroundImportSpawner Failing()   => new(new(BackgroundImportStatus.Failed,     "/tmp/import-x.log", 3, "exit 3"));

    public BackgroundImportLaunch Spawn(BackgroundImportRequest request) { Seen = request; Spawns++; return result; }
}
