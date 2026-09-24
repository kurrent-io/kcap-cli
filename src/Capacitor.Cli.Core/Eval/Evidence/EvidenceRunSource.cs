namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed record EvidenceRunSource(string SourceId, string Kind, bool Available, long FirstRevision, long RevisionCutoff, int? TurnCount);
