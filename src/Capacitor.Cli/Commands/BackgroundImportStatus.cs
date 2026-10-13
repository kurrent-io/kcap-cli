namespace Capacitor.Cli.Commands;

internal enum BackgroundImportStatus { NotNeeded, Running, ExitedZero, Failed }

internal static class BackgroundImportStatusExtensions {
    public static string Wire(this BackgroundImportStatus status) => status switch {
        BackgroundImportStatus.NotNeeded  => "not_needed",
        BackgroundImportStatus.Running    => "running",
        BackgroundImportStatus.ExitedZero => "exited_zero",
        BackgroundImportStatus.Failed     => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
