using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Builds the orientation once per run within its byte allowance: the scope sentence, its completeness, the manifest,
/// the root's first outline page and the call summary. Its pages are every retrieval question's seeded pages.</summary>
public sealed class EvidenceOrientationBuilder(EvidenceReadClient reader) {
    public const string ScopeSentence      = "This evaluation reads this session and its subagent lanes. Separately recorded child sessions and continuations are outside it.";
    public const string SummaryUnavailable = "The call summary is unavailable; every event is still reachable through the tools.";
    public const string SummaryBusy        = "The call summary is unavailable (the index is busy); every event is still reachable through the tools.";

    const string Separator = "\n\n";

    public async Task<EvidenceOrientation> BuildAsync(EvidenceScopeState scope, int orientationBytes, int pageBudgetBytes, CancellationToken ct) {
        var text   = new StringBuilder();
        var pages  = new List<JudgeLedgerPage>();
        var room   = orientationBytes;
        var budget = pageBudgetBytes.ToString(CultureInfo.InvariantCulture);

        void Add(string part) {
            var cut = Cut(part, room - Separator.Length);
            if (cut.Length == 0) return;
            text.Append(cut).Append(Separator);
            room -= Encoding.UTF8.GetByteCount(cut) + Separator.Length;
        }

        // A page enters only whole: one cut to fit would let the ledger claim evidence, bodies and handles the model never saw.
        bool AddPage(JudgeLedgerPage page) {
            if (page.Bytes + Separator.Length > room) return false;
            room -= page.Bytes + Separator.Length;
            text.Append(page.Text).Append(Separator);
            pages.Add(page);
            return true;
        }

        Add(ScopeSentence);
        Add(scope.Complete ? "Scope is complete over these sources." : $"Scope is incomplete: {string.Join(", ", scope.IncompleteReasons)}.");
        AddPage(EvidencePageRenderer.RenderSources(0, JudgeCiteHandles.Seeded(pages.Count), """{"from":0}""", [.. scope.Sources.Select(EvidenceRunSource.From)], 0));

        var outlined = 0;
        var unfinished = 0;
        if (scope.Sources.FirstOrDefault(s => s.Kind == "root" && s.IsAvailable) is { } root) {
            var outline = await reader.GetAsync("evidence-turns", [("token", scope.Token), ("source", root.SourceId), ("budget_bytes", budget)], ct);
            if (outline.Status is 404 or 409) return EvidenceOrientation.Failed(outline.Status);
            var args = "{\"source\":\"" + JsonEncodedText.Encode(root.SourceId) + "\"}";
            if (outline.IsSuccess && TryRender(JudgeCiteHandles.Seeded(pages.Count), "list_turns", args, outline.Body) is { } page && AddPage(page)) {
                outlined   = page.Turns.Count;
                unfinished = page.HasNext ? 1 : 0;
            } else {
                unfinished = 1;
            }
        }

        var summary = await reader.GetAsync("evidence-calls/summary", [("token", scope.Token), ("budget_bytes", budget)], ct);
        if (summary.Status is 404 or 409) return EvidenceOrientation.Failed(summary.Status);
        if (summary.Status == 503) Add(SummaryBusy);
        else if (!summary.IsSuccess || IndexState(summary.Body) == "unavailable" || TryRender(JudgeCiteHandles.Seeded(pages.Count), "summarize_calls", "{}", summary.Body) is not { } rendered)
            Add(SummaryUnavailable);
        else AddPage(rendered);

        return new EvidenceOrientation(text.ToString(), pages, outlined, unfinished, null);
    }

    // A success whose body is not a JSON object is left out like an unavailable read.
    static JudgeLedgerPage? TryRender(string handle, string tool, string args, string body) {
        try { return EvidencePageRenderer.Render(0, handle, tool, args, body); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    static string? IndexState(string body) {
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Str("index_state"); }
        catch (JsonException) { return null; }
    }

    static string Cut(string s, int room) {
        if (room <= 0) return "";
        var take = s;
        while (take.Length > 0 && Encoding.UTF8.GetByteCount(take) > room) take = take[..(take.Length / 2)];
        return take;
    }
}
