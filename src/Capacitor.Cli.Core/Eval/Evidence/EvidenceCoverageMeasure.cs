using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One evidence-route question's coverage, from its ledger and the bound manifest alone.</summary>
public static class EvidenceCoverageMeasure {
    public static readonly IReadOnlySet<string> Undelivered = new HashSet<string>(StringComparer.Ordinal) {
        EvalOmissionKinds.TurnsNotFetched, EvalOmissionKinds.PagesNotFetched, EvalOmissionKinds.BodiesNotFetched, EvalOmissionKinds.SourcesNotConsulted
    };

    static readonly IReadOnlySet<string> BudgetStops = new HashSet<string>(StringComparer.Ordinal) {
        EvalStopReasons.ByteBudget, EvalStopReasons.ToolCallBudget, EvalStopReasons.TimeBudget
    };

    public static EvalEvidenceCoverage ForRetrieval(EvidenceScopeState scope, JudgeLedger ledger, IReadOnlyList<EvalEvidenceCitation> citations) {
        var available = scope.Sources.Where(s => s.IsAvailable && !ledger.SourcesRefused.Contains(s.SourceId)).ToList();
        var omissions = ScopeOmissions(scope);

        Add(omissions, EvalOmissionKinds.TurnsNotFetched, OutlinedTurns(ledger).Count(t => t.Value.RangeState == "ok" && !Covered(ledger, t.Key.Source, t.Value.Start, t.Value.End)));
        Add(omissions, EvalOmissionKinds.PagesNotFetched, ledger.Pages.Count(p => p.HasNext && !ledger.FollowedHandles.Contains(p.Handle)));
        Add(omissions, EvalOmissionKinds.BodiesNotFetched, UnopenedBodies(ledger).Count);
        Add(omissions, EvalOmissionKinds.SourcesNotConsulted, available.Count(s => !ledger.SourcesWithPage.Contains(s.SourceId)));

        // An empty record claims every event was delivered, and events nobody outlined or paged leave none of the counts above.
        if (!omissions.Any(o => Undelivered.Contains(o.Kind))) {
            var partlyRead = available.Count(s => ledger.DeliveredEvents.Count(e => e.Source == s.SourceId) < s.RevisionCutoff - s.FirstRevision + 1);
            if (partlyRead > 0) omissions.Add(new EvalEvidenceOmission { Kind = EvalOmissionKinds.PagesNotFetched, Count = partlyRead, Detail = "unread_ranges" });
        }

        // A moved scope ends the run before coverage is measured, so only a vocabulary stop is taken from the footer.
        var footerStop = ledger.StopReason is { } stop && EvalStopReasons.All.Contains(stop) ? stop : null;
        return new EvalEvidenceCoverage {
            PolicyVersion      = EvalService.EvidenceCoveragePolicyVersion,
            ScopeVersion       = scope.ScopeVersion,
            SourcesConsulted   = [.. scope.Sources.Select(s => s.SourceId).Where(ledger.SourcesWithPage.Contains)],
            SourcesUnavailable = Unavailable(scope, ledger.SourcesRefused),
            Citations          = [.. citations],
            Omissions          = omissions,
            StopReason         = footerStop ?? (omissions.Any(o => Undelivered.Contains(o.Kind)) ? EvalStopReasons.JudgeStopped : null)
        };
    }

    /// <summary>A fitting one-shot trace holds every canonical body of every available source.</summary>
    public static EvalEvidenceCoverage ForOneShot(EvidenceScopeState scope, IReadOnlyList<EvalEvidenceCitation> citations) => new() {
        PolicyVersion      = EvalService.EvidenceCoveragePolicyVersion,
        ScopeVersion       = scope.ScopeVersion,
        SourcesConsulted   = [.. scope.Sources.Where(s => s.IsAvailable).Select(s => s.SourceId)],
        SourcesUnavailable = Unavailable(scope, FrozenSet<string>.Empty),
        Citations          = [.. citations],
        Omissions          = ScopeOmissions(scope)
    };

    public static EvalTraceCoverage RetrievalTraceCoverage(JudgeLedger ledger, int numTurns, int maxTurns) {
        var budgets   = ledger.Header?.Budgets ?? throw new InvalidDataException("the ledger has no header");
        var delivered = ledger.Footer?.DeliveredBytes ?? ledger.Pages.Where(p => p.Handle.StartsWith('p')).Sum(p => (long)p.Bytes);
        return EvalTraceCoverage.ForEvidenceRetrieval(
            budgetTripped:  ledger.StopReason is { } stop && BudgetStops.Contains(stop),
            deliveredBytes: Math.Clamp(delivered, 0, budgets.JudgeByteBudgetBytes),
            budgetBytes:    budgets.JudgeByteBudgetBytes,
            iterationsUsed: Math.Clamp(numTurns, 0, maxTurns),
            maxIterations:  maxTurns,
            toolCalls:      Math.Clamp(ledger.ToolCalls, 0, budgets.MaxToolCalls),
            maxToolCalls:   budgets.MaxToolCalls);
    }

    /// <summary>The harness stopped at its turn cap: the cap, the turns the manifest knows of, and the distinct known turns the
    /// ledger delivered, never more than that total.</summary>
    public static EvalQuestionFailure IterationCap(string category, string questionId, EvidenceScopeState scope, JudgeLedger ledger, int maxTurns) {
        var available = scope.Sources.Where(s => s.IsAvailable).ToList();
        var known     = available.Where(s => s.TurnCount is not null).Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);
        var total     = available.Sum(s => s.TurnCount ?? 0);
        var fetched   = ledger.DeliveredTurns.Count(t => known.Contains(t.Source));
        return new EvalQuestionFailure {
            Category = category, QuestionId = questionId, Code = EvalFailureCodes.IterationCap,
            MaxIterations = maxTurns, TurnsTotal = total, TurnsFetched = Math.Min(fetched, total)
        };
    }

    /// <summary>Every turn row a delivered <c>list_turns</c> page carried, read back from the page text; a re-read turn keeps
    /// its latest row.</summary>
    internal static IReadOnlyDictionary<(string Source, int Index), (long Start, long End, string RangeState)> OutlinedTurns(JudgeLedger ledger) {
        var turns = new Dictionary<(string Source, int Index), (long Start, long End, string RangeState)>();
        foreach (var page in ledger.Pages) {
            if (page.Tool != "list_turns" || page.Source is not { } source) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(page.Text); } catch (JsonException) { continue; }
            using (doc) {
                if (doc.RootElement.Arr("turns") is not { } rows) continue;
                foreach (var t in rows.EnumerateArray())
                    if (t.Num("index") is { } index && t.Num("start_revision") is { } start && t.Num("end_revision") is { } end && t.Str("range_state") is { } state)
                        turns[(source, (int)index)] = (start, end, state);
            }
        }
        return turns;
    }

    /// <summary>Canonical bodies a page left as descriptors that no read_body page opened.</summary>
    internal static IReadOnlySet<string> UnopenedBodies(JudgeLedger ledger) {
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        var opened   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in ledger.Pages)
            foreach (var (reference, field, ordinal) in page.Bodies)
                (page.Tool == "read_body" ? opened : deferred).Add(EvidenceCanonicalContent.BodyKey(reference, field, ordinal));
        deferred.ExceptWith(opened);
        return deferred;
    }

    static bool Covered(JudgeLedger ledger, string source, long start, long end) {
        for (var r = start; r <= end; r++)
            if (!ledger.DeliveredEvents.Contains((source, r))) return false;
        return true;
    }

    static List<EvalEvidenceOmission> ScopeOmissions(EvidenceScopeState scope) =>
        [.. scope.IncompleteReasons.Select(r => new EvalEvidenceOmission { Kind = EvalOmissionKinds.ScopeIncomplete, Count = 1, Detail = r })];

    static List<string> Unavailable(EvidenceScopeState scope, IReadOnlySet<string> refused) =>
        [.. scope.Sources.Where(s => !s.IsAvailable || refused.Contains(s.SourceId)).Select(s => s.SourceId)];

    static void Add(List<EvalEvidenceOmission> omissions, string kind, int count) {
        if (count > 0) omissions.Add(new EvalEvidenceOmission { Kind = kind, Count = count });
    }
}
