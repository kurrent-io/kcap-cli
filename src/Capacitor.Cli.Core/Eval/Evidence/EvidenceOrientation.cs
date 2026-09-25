namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The once-per-run orientation: its text, the pages it seeded, how many turns it outlined and whether the root's
/// outline was left unfinished; or the status of a read that failed.</summary>
public sealed record EvidenceOrientation(string Text, IReadOnlyList<JudgeLedgerPage> Pages, int OutlinedTurns, int UnfinishedOutlines, int? FailedStatus) {
    public static EvidenceOrientation Failed(int status) => new("", [], 0, 0, status);

    public IReadOnlyList<(string Source, int Index)> OutlineTurns => [.. Pages.SelectMany(p => p.Turns)];
}
