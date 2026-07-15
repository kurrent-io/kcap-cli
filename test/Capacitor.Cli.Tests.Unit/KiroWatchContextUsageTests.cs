using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1286 Phase 7 — the live-watcher side of Kiro context-%: <see cref="WatchCommand.EnrichKiroContextUsage"/>
/// reads the sibling <c>{id}.json</c> (derived from the TRANSCRIPT path, not the dashless session id)
/// and stamps <c>data._kcap_usage.context_usage_percentage</c> onto AssistantMessage lines at flush,
/// reusing the import path's <see cref="Capacitor.Cli.Core.Kiro.KiroUsage"/>. Best-effort + order-preserving.
/// </summary>
public class KiroWatchContextUsageTests {
    // One turn, anchor message id "a2", context% 5.2612 (mirrors KiroUsageTests).
    const string Meta = """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["u1","a1","t1","a2"],
           "context_usage_percentage":5.2612,
           "metering_usage":[{"value":0.25,"unit":"credit"},{"value":0.5,"unit":"credit"}]}
        ]}}}
        """;
    const string AnchorLine  = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"}]}}""";
    const string PromptLine   = """{"version":"v1","kind":"Prompt","data":{"message_id":"a2","content":[]}}""";
    const string NonAnchorAsst = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"x"}]}}""";

    // Writes the sibling {stem}.json and returns the (non-existent-is-fine) {stem}.jsonl transcript path.
    // stem is a DASHED id to prove the sibling is derived from the transcript path, not a dashless session id.
    static (string transcriptPath, string dir) SeedSibling(string metaJson) {
        var dir  = Path.Combine(Path.GetTempPath(), "kcap-kiro-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var stem = "11111111-2222-3333-4444-555555555555";   // dashed, like Kiro's on-disk files
        File.WriteAllText(Path.Combine(dir, stem + ".json"), metaJson);
        return (Path.Combine(dir, stem + ".jsonl"), dir);
    }

    [Test]
    public async Task Enriches_anchor_assistant_line_from_sibling_json() {
        var (transcriptPath, dir) = SeedSibling(Meta);
        try {
            var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
            await Assert.That(outLines[0]).Contains("_kcap_usage");
            await Assert.That(outLines[0]).Contains("5.2612");
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Leaves_non_anchor_and_non_assistant_lines_untouched() {
        var (transcriptPath, dir) = SeedSibling(Meta);
        try {
            var outLines = WatchCommand.EnrichKiroContextUsage([NonAnchorAsst, PromptLine], transcriptPath);
            await Assert.That(outLines[0]).DoesNotContain("_kcap_usage");  // assistant, but not the anchor turn
            await Assert.That(outLines[1]).DoesNotContain("_kcap_usage");  // Prompt line
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Missing_sibling_json_returns_lines_unchanged() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-kiro-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var transcriptPath = Path.Combine(dir, "no-sibling.jsonl");   // no {stem}.json next to it
            var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
            await Assert.That(outLines[0]).IsEqualTo(AnchorLine);          // untouched, best-effort
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Malformed_sibling_json_returns_lines_unchanged() {
        var (transcriptPath, dir) = SeedSibling("{ not valid json");
        try {
            var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
            await Assert.That(outLines[0]).IsEqualTo(AnchorLine);
        } finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Preserves_batch_order_and_count_when_flushing_a_buffer() {
        // Simulates enriching a flushed buffer (finding 2): a mixed batch keeps order + count,
        // with only the anchor assistant line enriched.
        var (transcriptPath, dir) = SeedSibling(Meta);
        try {
            var batch = new List<string> { PromptLine, NonAnchorAsst, AnchorLine };
            var outLines = WatchCommand.EnrichKiroContextUsage(batch, transcriptPath);
            await Assert.That(outLines.Count).IsEqualTo(3);
            await Assert.That(outLines[0]).DoesNotContain("_kcap_usage");
            await Assert.That(outLines[1]).DoesNotContain("_kcap_usage");
            await Assert.That(outLines[2]).Contains("5.2612");            // anchor stays last, enriched
        } finally { Directory.Delete(dir, true); }
    }
}
