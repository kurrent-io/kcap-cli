using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>A parsed ledger and what it delivered: the sets coverage, cite expansion and S8 read.</summary>
public sealed class JudgeLedger {
    readonly Dictionary<string, string> _cites = new(StringComparer.Ordinal);

    public JudgeLedger(JudgeLedgerHeader? header, IReadOnlyList<JudgeLedgerPage> pages, IReadOnlyList<JudgeLedgerCall> calls, JudgeLedgerFooter? footer) {
        Header = header; Pages = pages; Calls = calls; Footer = footer;
        var events   = new HashSet<(string, long)>();
        var turns    = new HashSet<(string, int)>();
        var sources  = new HashSet<string>(StringComparer.Ordinal);
        var followed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pages) {
            foreach (var (h, r) in p.Cites) _cites[h] = r;
            foreach (var (s, from, to) in p.Revisions) for (var r = from; r <= to; r++) events.Add((s, r));
            foreach (var t in p.Turns) turns.Add(t);
            if (p.Source is { } source && p.Tool is "list_turns" or "read_events") sources.Add(source);
            using var args = JsonDocument.Parse(p.ArgsJson);
            if (args.RootElement.Bool("next") == true && args.RootElement.Str("page") is { } continued) followed.Add(continued);
        }
        DeliveredEvents = events; DeliveredTurns = turns; SourcesWithPage = sources; FollowedHandles = followed;
        SourcesRefused  = footer is null ? System.Collections.Frozen.FrozenSet<string>.Empty : footer.SourcesRefused.ToHashSet(StringComparer.Ordinal);
    }

    public JudgeLedgerHeader?             Header { get; }
    public IReadOnlyList<JudgeLedgerPage> Pages  { get; }
    public IReadOnlyList<JudgeLedgerCall> Calls  { get; }
    public JudgeLedgerFooter?             Footer { get; }

    public IReadOnlyDictionary<string, string>        Cites           => _cites;
    public IReadOnlySet<(string Source, long Revision)> DeliveredEvents { get; }
    public IReadOnlySet<(string Source, int Index)>   DeliveredTurns  { get; }
    public IReadOnlySet<string>                       SourcesWithPage { get; }
    public IReadOnlySet<string>                       FollowedHandles { get; }
    public IReadOnlySet<string>                       SourcesRefused  { get; }

    public int    ToolCalls      => Footer?.ToolCalls ?? Calls.Count(c => c.Outcome != JudgeLedgerOutcomes.NotExecuted);
    public long   DeliveredBytes => Footer?.DeliveredBytes ?? Pages.Sum(p => (long)p.Bytes);
    public string? StopReason    => Footer?.StopReason;

    public bool IsDelivered(EvidenceRefText r) => r.Form switch {
        EvidenceRefForm.Event => DeliveredEvents.Contains((r.SourceId, r.A)),
        EvidenceRefForm.Range => r.B - r.A < 100_000 && Enumerable.Range(0, (int)(r.B - r.A + 1)).All(i => DeliveredEvents.Contains((r.SourceId, r.A + i))),
        _                     => DeliveredTurns.Contains((r.SourceId, (int)r.B))
    };

    /// <summary>A handle this ledger minted expands to its ref; a literal ref only when this ledger delivered it.</summary>
    public bool TryExpand(string token, out string canonicalRef) {
        if (_cites.TryGetValue(token, out canonicalRef!)) return true;
        if (EvidenceRefText.TryParse(token, out var r) && IsDelivered(r)) { canonicalRef = r.ToString(); return true; }
        canonicalRef = "";
        return false;
    }
}
