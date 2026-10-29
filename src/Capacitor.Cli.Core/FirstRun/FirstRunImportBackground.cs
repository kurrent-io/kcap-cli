namespace Capacitor.Cli.Core.FirstRun;

/// <summary>The background child's status as the import outcome reports it.</summary>
public static class FirstRunImportBackground {
    public const string NotNeeded  = "not_needed";
    public const string Running    = "running";
    public const string ExitedZero = "exited_zero";
    public const string Failed     = "failed";

    public static readonly IReadOnlyList<string> All = [NotNeeded, Running, ExitedZero, Failed];

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}
