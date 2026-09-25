namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceRunSource(string SourceId, string Kind, bool Available, long FirstRevision, long RevisionCutoff, int? TurnCount) {
    public static EvidenceRunSource From(EvidenceSourceDto source) =>
        new(source.SourceId, source.Kind, source.IsAvailable, source.FirstRevision, source.RevisionCutoff, source.TurnCount);
}
