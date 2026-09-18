namespace Capacitor.Cli.Commands;

internal sealed record BackgroundImportLaunch(BackgroundImportStatus Status, string? LogPath, int? ExitCode, string? Error) {
    public static BackgroundImportLaunch NotNeeded { get; } = new(BackgroundImportStatus.NotNeeded, null, null, null);
}
