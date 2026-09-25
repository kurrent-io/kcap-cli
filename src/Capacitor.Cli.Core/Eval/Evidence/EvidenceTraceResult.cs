using System.Collections.Frozen;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The one-shot fit test's outcome: whether the whole trace fits, the trace with its cites and entry spans when it
/// does, the characters seen, the event reads it took, and the status of a read that failed (0 when the server was
/// unreachable).</summary>
public sealed record EvidenceTraceResult(
        bool Fits, string TraceJson, int Chars, long TotalChars,
        IReadOnlyDictionary<string, string> Cites,
        IReadOnlyList<(string Source, long Revision, int Offset, int Length)> Detail,
        int Reads, int? FailedStatus) {
    public static EvidenceTraceResult TooLarge(long totalChars, int reads) =>
        new(false, "", 0, totalChars, FrozenDictionary<string, string>.Empty, [], reads, null);

    public static EvidenceTraceResult Failed(int status, int reads) =>
        new(false, "", 0, 0, FrozenDictionary<string, string>.Empty, [], reads, status);
}
