using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The retrospective's evidence: a scope and coverage summary, then every ref the judges cited re-read under the bound
/// scope and cut to a bounded excerpt. An unreadable ref, or an answer that does not parse, leaves a fixed line; a moved scope
/// is returned as a 409.</summary>
public sealed class EvidenceRetrospectiveInputs(EvidenceReadClient reader) {
    public const int    ExcerptChars     = 1_500;
    public const string NoLongerReadable = "[no longer readable]";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<(string Trace, int? FailedStatus)> BuildTraceAsync(
            EvidenceScopeState scope, IReadOnlyList<EvalQuestionAssessment> assessments, int excerptBudgetBytes,
            IReadOnlyDictionary<string, IReadOnlyList<EvalEvidenceCitation>>? obligationCitations, CancellationToken ct) {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var a in assessments.OrderBy(Rank).ThenBy(a => a.QuestionId, StringComparer.Ordinal)) {
            foreach (var r in (a.EvidenceCoverage?.Citations ?? []).Select(c => c.Ref))
                if (seen.Add(r)) order.Add(r);
            if (obligationCitations is not null && obligationCitations.TryGetValue(a.QuestionId, out var obligations))
                foreach (var r in obligations.Select(c => c.Ref))
                    if (seen.Add(r)) order.Add(r);
        }

        var block = new StringBuilder();
        int used = 0, shown = 0;
        foreach (var reference in order) {
            var (excerpt, failed) = await ExcerptAsync(scope.Token, reference, ct);
            if (failed is { } status) return ("", status);
            var line  = $"{reference}: {excerpt}\n";
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (used + bytes > excerptBudgetBytes && shown > 0) break;
            block.Append(line);
            used += bytes;
            shown++;
        }

        var header = order.Count == shown
            ? $"Cited evidence ({shown}):\n"
            : $"Cited evidence ({shown} of {order.Count} shown; {order.Count - shown} omitted for space):\n";
        return (ScopeSummary(scope, assessments) + "\n\n" + header + block, null);
    }

    public static string ScopeSummary(EvidenceScopeState scope, IReadOnlyList<EvalQuestionAssessment> assessments) {
        var completeness = scope.Complete ? "complete over the root and its subagent lanes" : $"incomplete ({string.Join(", ", scope.IncompleteReasons)})";
        var stops = assessments.Select(a => a.EvidenceCoverage?.StopReason).Where(s => s is not null).Distinct().ToList();
        return $"Scope {scope.ScopeVersion}: {scope.Sources.Count} source(s), {completeness}. "
             + (stops.Count > 0 ? $"Budget stops: {string.Join(", ", stops)}." : "No budget stops.");
    }

    // Non-pass assessed first, then unassessed, then passing.
    static int Rank(EvalQuestionAssessment a) => (a.Outcome ?? EvalOutcomes.Assessed) != EvalOutcomes.Assessed ? 1 : a.Verdict == "pass" ? 2 : 0;

    async Task<(string Excerpt, int? Failed)> ExcerptAsync(string token, string reference, CancellationToken ct) {
        if (!EvidenceRefText.TryParse(reference, out var parsed)) return (NoLongerReadable, null);
        if (parsed.Form != EvidenceRefForm.Turn) return await EventsExcerptAsync(token, reference, ct);

        var outline = await reader.GetAsync("evidence-turns", [("token", token), ("source", parsed.SourceId), ("from_index", parsed.B.ToString(Inv)), ("take", "1")], ct);
        if (outline.Status == 409) return ("", 409);
        if (outline.IsSuccess && EventsRefOf(outline.Body, parsed.B) is { } window) {
            var (excerpt, failed) = await EventsExcerptAsync(token, window, ct);
            if (failed is not null || excerpt != NoLongerReadable) return (excerpt, failed);
        }

        var card = await reader.GetAsync("evidence-body", [("token", token), ("ref", reference), ("field", "card"), ("offset", "0"), ("max_bytes", ExcerptChars.ToString(Inv))], ct);
        if (card.Status == 409) return ("", 409);
        return (card.IsSuccess && Content(card.Body) is { } content ? Cut(content) : NoLongerReadable, null);
    }

    async Task<(string Excerpt, int? Failed)> EventsExcerptAsync(string token, string reference, CancellationToken ct) {
        var page = await reader.GetAsync("evidence-events", [("token", token), ("ref", reference)], ct);
        if (page.Status == 409) return ("", 409);
        if (!page.IsSuccess) return (NoLongerReadable, null);
        using var doc = TryParse(page.Body);
        if (doc is null) return (NoLongerReadable, null);
        var texts = new List<string>();
        if (doc.RootElement.Arr("entries") is { } entries)
            foreach (var e in entries.EnumerateArray()) {
                if (e.Str("kind") != "plan_entry") { texts.Add(e.Str("text") ?? e.Str("output") ?? e.Str("event_type") ?? ""); continue; }
                var (plan, failed) = await PlanExcerptAsync(token, e, ct);
                if (failed is not null) return ("", failed);
                texts.Add(plan);
            }
        return (Cut(string.Join(" ", texts)), null);
    }

    // A plan entry's substance is its plan_content and its document text, either of which may be a deferred body.
    async Task<(string Excerpt, int? Failed)> PlanExcerptAsync(string token, JsonElement entry, CancellationToken ct) {
        var parts = new List<string> { entry.Str("event_type") ?? "" };
        if (entry.Prop("plan_content") is { IsNull: false } content) parts.Add(Cut(content.GetRawText()));
        if (entry.Str("text") is { } text) parts.Add(Cut(text));
        foreach (var (bodyRef, field, ordinal) in EvidenceCanonicalContent.Deferred(entry)) {
            List<(string Key, string Value)> query = [("token", token), ("ref", bodyRef), ("field", field), ("offset", "0"), ("max_bytes", ExcerptChars.ToString(Inv))];
            if (ordinal is { } o) query.Add(("ordinal", o.ToString(Inv)));
            var body = await reader.GetAsync("evidence-body", query, ct);
            if (body.Status == 409) return ("", 409);
            if (body.IsSuccess && Content(body.Body) is { Length: > 0 } deferred) parts.Add(Cut(deferred));
        }
        return (string.Join(" ", parts.Where(p => p.Length > 0)), null);
    }

    static string? EventsRefOf(string body, long index) {
        using var doc = TryParse(body);
        if (doc?.RootElement.Arr("turns") is not { } turns) return null;
        foreach (var t in turns.EnumerateArray())
            if (t.Num("index") == index) return t.Str("events_ref");
        return null;
    }

    static string? Content(string body) {
        using var doc = TryParse(body);
        return doc is null ? null : doc.RootElement.Str("content") ?? "";
    }

    static JsonDocument? TryParse(string body) {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    static string Cut(string s) => s.Length <= ExcerptChars ? s : s[..ExcerptChars];
}
