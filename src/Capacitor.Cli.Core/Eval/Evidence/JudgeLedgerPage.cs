using System.Text;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One delivered page: the exact text the judge received and what it delivered. <see cref="Detail"/> spans are
/// UTF-8 byte ranges of <see cref="Text"/>; <see cref="Bodies"/> on a read_body page is the body it opened, on any other
/// page the canonical bodies it left as descriptors.</summary>
public sealed record JudgeLedgerPage(
        int Seq, string Handle, string Tool, string ArgsJson, string? Source, string Text,
        IReadOnlyList<(string Source, long From, long To)> Revisions,
        IReadOnlyList<(string Source, int Index)> Turns,
        IReadOnlyList<(string Ref, string Field, int? Ordinal)> Bodies,
        IReadOnlyList<(string Source, long Revision, int Offset, int Length)> Detail,
        IReadOnlyDictionary<string, string> Cites,
        bool HasNext, string? Next) {
    public int Bytes { get; } = Encoding.UTF8.GetByteCount(Text);

    public bool Equals(JudgeLedgerPage? other) =>
        other is not null && Seq == other.Seq && Handle == other.Handle && Tool == other.Tool && ArgsJson == other.ArgsJson && Source == other.Source
     && Text == other.Text && HasNext == other.HasNext && Next == other.Next
     && Revisions.SequenceEqual(other.Revisions) && Turns.SequenceEqual(other.Turns) && Bodies.SequenceEqual(other.Bodies) && Detail.SequenceEqual(other.Detail)
     && Cites.Count == other.Cites.Count && Cites.All(kv => other.Cites.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Seq, Handle, Text);
}
