namespace Capacitor.Cli.Commands;

/// <summary>How each selected id's own import call ended. Succeeded includes a call that returned
/// Skipped after posting a carried child's content: the session landed work either way.</summary>
internal sealed record ImportRunPartition(
    IReadOnlyList<string> SucceededIds,
    IReadOnlyList<string> SkippedIds,
    IReadOnlyList<string> FailedIds) {
    public static ImportRunPartition Empty { get; } = new([], [], []);
}
