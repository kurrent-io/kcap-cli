using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

public static class JudgeLedgerReader {
    /// <summary>Reads a ledger another process may still be appending to; a final line torn by a kill is ignored. A call is
    /// written before the footer that counts it, so calls a kill left after the last footer are added to its totals.</summary>
    public static JudgeLedger Read(string path) {
        JudgeLedgerHeader? header = null;
        JudgeLedgerFooter? footer = null;
        var pages = new List<JudgeLedgerPage>();
        var calls = new List<JudgeLedgerCall>();
        var after = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc) {
                var e = doc.RootElement;
                switch (e.Str("kind")) {
                    case "header": header = ReadHeader(e); break;
                    case "page":   pages.Add(ReadPage(e)); break;
                    case "call":   calls.Add(ReadCall(e)); after++; break;
                    case "footer": footer = ReadFooter(e); after = 0; break;
                }
            }
        }
        if (footer is not null && after > 0) footer = Extend(footer, calls[^after..]);
        return new JudgeLedger(header, pages, calls, footer);
    }

    static JudgeLedgerFooter Extend(JudgeLedgerFooter footer, IReadOnlyList<JudgeLedgerCall> uncounted) => footer with {
        ToolCalls      = footer.ToolCalls + uncounted.Count(c => c.Outcome != JudgeLedgerOutcomes.NotExecuted),
        DeliveredBytes = footer.DeliveredBytes + uncounted.Sum(c => (long)c.Bytes),
        StopReason     = uncounted.LastOrDefault(c => c.StopReason is not null)?.StopReason ?? footer.StopReason
    };

    public static JudgeLedgerPage ReadPage(JsonElement e) => new(
        (int)e.GetProperty("seq").GetInt64(), e.GetProperty("handle").GetString()!, e.GetProperty("tool").GetString()!,
        e.GetProperty("args").GetRawText(), e.Str("source"), e.GetProperty("text").GetString()!,
        [.. e.GetProperty("revisions").EnumerateArray().Select(a => (a[0].GetString()!, a[1].GetInt64(), a[2].GetInt64()))],
        [.. e.GetProperty("turns").EnumerateArray().Select(a => (a[0].GetString()!, a[1].GetInt32()))],
        [.. e.GetProperty("bodies").EnumerateArray().Select(a => (a[0].GetString()!, a[1].GetString()!, a[2].IsNumber ? a[2].GetInt32() : (int?)null))],
        [.. e.GetProperty("detail").EnumerateArray().Select(a => (a[0].GetString()!, a[1].GetInt64(), a[2].GetInt32(), a[3].GetInt32()))],
        e.GetProperty("cites").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
        e.GetProperty("has_next").GetBoolean(), e.Str("next"));

    internal static EvidenceRunBudgets ReadBudgets(JsonElement b) => new(
        b.GetProperty("max_tool_calls").GetInt32(), b.GetProperty("judge_byte_budget_bytes").GetInt64(), b.GetProperty("page_budget_bytes").GetInt32());

    static JudgeLedgerHeader ReadHeader(JsonElement e) => new(
        e.GetProperty("eval_run_id").GetString()!, e.GetProperty("question_id").GetString()!, e.GetProperty("scope_version").GetString()!,
        ReadBudgets(e.GetProperty("budgets")),
        e.GetProperty("soft_deadline").IsNull ? null : e.GetProperty("soft_deadline").GetDateTimeOffset(),
        e.GetProperty("started_at").GetDateTimeOffset());

    static JudgeLedgerCall ReadCall(JsonElement e) => new(
        e.GetProperty("seq").GetInt32(), e.GetProperty("tool").GetString()!, e.GetProperty("args").GetRawText(),
        e.GetProperty("outcome").GetString()!, e.Str("stop_reason"), e.Str("error"), e.GetProperty("bytes").GetInt32());

    static JudgeLedgerFooter ReadFooter(JsonElement e) => new(
        e.GetProperty("tool_calls").GetInt32(), e.GetProperty("delivered_bytes").GetInt64(), e.Str("stop_reason"),
        [.. e.GetProperty("sources_refused").EnumerateArray().Select(s => s.GetString()!)], e.GetProperty("ended_at").GetDateTimeOffset());
}
