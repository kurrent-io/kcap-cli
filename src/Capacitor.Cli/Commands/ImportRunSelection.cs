namespace Capacitor.Cli.Commands;

/// <summary>Published once selection is fixed and before any import work runs.</summary>
internal sealed record ImportRunSelection(
    IReadOnlyList<string> RunCandidateIds,
    IReadOnlyList<string> SelectedIds,
    bool                  RemainderExists) {
    public static ImportRunSelection Empty { get; } = new([], [], RemainderExists: false);
}
