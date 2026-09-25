using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Fetches a strategy's first view under the bound token and turns each section's embedded evidence page into a seeded
/// ledger page, so its rows are citable and its continuation opens like any other page.</summary>
public static class EvidenceFirstViewReader {
    public const string Route = "evidence-first-view";

    static readonly IReadOnlyDictionary<string, string> ToolOfKind = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["turns"] = "list_turns", ["events"] = "read_events", ["calls"] = "list_calls", ["authorizations"] = "list_authorizations"
    };

    /// <summary>The view when it was built, else none; <c>FailedStatus</c> is set only for a 404 or 409, which mean the scope is
    /// gone. A view whose rendered block would exceed the request's budget is dropped whole, as the server's own runner does.</summary>
    public static async Task<(EvidenceFirstView? View, int? FailedStatus)> ReadAsync(EvidenceReadClient reader, string token, string strategy, int firstSeededOrdinal, CancellationToken ct) {
        var result = await reader.GetAsync(Route, [("token", token), ("strategy", strategy), ("budget_bytes", EvidenceBudgets.FirstViewBytes.ToString(CultureInfo.InvariantCulture))], ct);
        if (result.Status is 404 or 409) return (null, result.Status);
        if (!result.IsSuccess) return (null, null);

        try {
            using var doc = JsonDocument.Parse(result.Body);
            var root = doc.RootElement;
            if (root.Str("state") != "built" || root.Str("strategy") is not { } resolved || root.Str("strategy_version") is not { } version) return (null, null);

            var text = new StringBuilder($"First view ({resolved}):\n");
            if (root.Str("guidance") is { Length: > 0 } guidance) text.Append(guidance).Append('\n');
            var pages = new List<JudgeLedgerPage>();
            if (root.Arr("sections") is { } sections)
                foreach (var section in sections.EnumerateArray()) {
                    if (section.Str("kind") is not { } kind || !ToolOfKind.TryGetValue(kind, out var tool) || section.Obj(kind) is not { } page) continue;
                    var purpose  = section.Str("purpose") ?? kind;
                    var args     = "{\"section\":\"" + JsonEncodedText.Encode(purpose) + "\"}";
                    var rendered = EvidencePageRenderer.Render(0, JudgeCiteHandles.Seeded(firstSeededOrdinal + pages.Count), tool, args, page.GetRawText());
                    pages.Add(rendered);
                    text.Append("## ").Append(purpose).Append('\n').Append(rendered.Text).Append('\n');
                }
            if (root.Arr("omitted_sections") is { } omitted && omitted.GetArrayLength() > 0)
                text.Append("Not shown, each one read away: ")
                    .Append(string.Join(", ", omitted.EnumerateArray().Select(o => o.Str("reference") is { } r ? $"{o.Str("purpose")} ({r})" : o.Str("purpose"))))
                    .Append('\n');

            var block = text.ToString();
            if (Encoding.UTF8.GetByteCount(block) > EvidenceBudgets.FirstViewBytes) return (null, null);
            return (new EvidenceFirstView(resolved, version, block, pages), null);
        } catch (JsonException) {
            return (null, null);
        }
    }
}
