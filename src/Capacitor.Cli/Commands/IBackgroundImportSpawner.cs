namespace Capacitor.Cli.Commands;

internal interface IBackgroundImportSpawner {
    BackgroundImportLaunch Spawn(BackgroundImportRequest request);
}
