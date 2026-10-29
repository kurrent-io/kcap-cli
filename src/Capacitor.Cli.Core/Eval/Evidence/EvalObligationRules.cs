using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Mirrors the server's obligation identity and reconciliation; the server re-validates what this produces, so the two
/// must agree byte for byte (the shared golden vectors pin it).</summary>
public static class EvalObligationRules {
    public static bool IsDecisive(string? status) => status is "verified" or "claimed" or "not_done" or "dropped";

    /// <summary>Trimmed, with every run of whitespace collapsed to one space.</summary>
    public static string Normalize(string title) {
        var sb = new StringBuilder(title.Length);
        var pendingSpace = false;
        foreach (var c in title) {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The anchor and the words only — never the status, the origin or arrival order — so the same obligation reported
    /// twice is one obligation.</summary>
    public static string DeriveId(string anchorRef, string title) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(anchorRef + "\n" + Normalize(title)));
        return "ob:" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>Turns what the judge reported into what persists: a total function of the set of reported entries, never of
    /// their order. <paramref name="certified"/> maps a cite handle to the ref and digest it certified to; an absent handle, or
    /// one without a digest, is uncertified.</summary>
    public static EvalQuestionAssessment Reconcile(EvalQuestionAssessment assessment, IReadOnlyList<EvalReportedObligation>? reported,
            IReadOnlyDictionary<string, EvalEvidenceCitation> certified, bool isReportingQuestion) {
        var outcome = assessment.Outcome ?? EvalOutcomes.Assessed;
        if (!isReportingQuestion || outcome == EvalOutcomes.NotApplicable || reported is null or { Count: 0 }) return assessment with { Obligations = null };

        var groups = new Dictionary<string, List<Anchored>>(StringComparer.Ordinal);
        foreach (var entry in reported) {
            if (Certify(certified, entry.Anchor) is not { } anchor) continue;
            var title = Normalize(entry.Title);
            if (title.Length == 0) continue;
            var id = DeriveId(anchor.Ref, title);
            if (!groups.TryGetValue(id, out var group)) groups[id] = group = [];
            group.Add(new(entry, anchor, title));
        }

        var obligations = new List<EvalObligationResult>(groups.Count);
        foreach (var (id, group) in groups) {
            var lead   = group.Min(AnchoredOrder)!;
            var status = group.All(g => g.Entry.Status == group[0].Entry.Status) ? group[0].Entry.Status : EvalObligationContract.Unverified;

            var citations = group
                .SelectMany(g => g.Entry.Citations)
                .Select(token => Certify(certified, token))
                .Where(c => c is not null && c.Ref != lead.Anchor.Ref)
                .Select(c => c!)
                .DistinctBy(c => c.Ref, StringComparer.Ordinal)
                .OrderBy(c => c.Ref, StringComparer.Ordinal)
                .Take(EvalObligationContract.MaxCitations)
                .ToList();

            if (IsDecisive(status) && citations.Count == 0) status = EvalObligationContract.Unverified;

            obligations.Add(new() {
                Id = id, Title = lead.Title, Origin = lead.Entry.Origin, Status = status, Anchor = lead.Anchor, Citations = citations, Note = lead.Entry.Note
            });
        }

        if (obligations.Count == 0) return assessment with { Obligations = null };

        obligations.Sort((a, b) => {
            var byAnchor = string.CompareOrdinal(a.Anchor.Ref, b.Anchor.Ref);
            return byAnchor != 0 ? byAnchor : string.CompareOrdinal(a.Id, b.Id);
        });

        return outcome == EvalOutcomes.Assessed && !obligations.Any(o => IsDecisive(o.Status))
            ? assessment with { Obligations = obligations, Outcome = EvalOutcomes.InsufficientEvidence, Score = null, Verdict = null }
            : assessment with { Obligations = obligations };
    }

    sealed record Anchored(EvalReportedObligation Entry, EvalEvidenceCitation Anchor, string Title);

    // Title, origin, note (null last), then the anchor ref: a total order, so the entry a merged obligation takes its words
    // from never depends on arrival order.
    static readonly IComparer<Anchored> AnchoredOrder = Comparer<Anchored>.Create(static (a, b) => {
        var c = string.CompareOrdinal(a.Title, b.Title);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Entry.Origin, b.Entry.Origin);
        if (c != 0) return c;
        c = (a.Entry.Note is null).CompareTo(b.Entry.Note is null);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Entry.Note, b.Entry.Note);
        return c != 0 ? c : string.CompareOrdinal(a.Anchor.Ref, b.Anchor.Ref);
    });

    static EvalEvidenceCitation? Certify(IReadOnlyDictionary<string, EvalEvidenceCitation> certified, string token) =>
        certified.TryGetValue(token, out var c) && c.Digest is not null ? c : null;
}
