namespace Capacitor.Cli.Commands;

internal sealed record BackgroundImportRequest(string RunId, string ProfileName, string DefaultVisibility, string WorkingDirectory);

internal interface IBackgroundImportSpawner {
    BackgroundImportLaunch Spawn(BackgroundImportRequest request);
}
