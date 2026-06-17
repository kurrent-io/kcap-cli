using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers <see cref="KiroUsage"/>: reading per-turn credits + context% from the
/// session's <c>{id}.json</c> metadata (anchored on the turn's final message id)
/// and injecting them onto the matching <c>AssistantMessage</c> transcript line.
/// </summary>
public class KiroUsageTests {
    // One turn: 4 message ids; credits 0.25 + 0.5 = 0.75 (exact in binary, so the
    // sum assertion stays float-safe); context% 5.2612. Anchor = the last id.
    const string Meta = """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["u1","a1","t1","a2"],
           "input_token_count":0,"output_token_count":0,
           "context_usage_percentage":5.2612,
           "metering_usage":[{"value":0.25,"unit":"credit"},{"value":0.5,"unit":"credit"}]}
        ]}}}
        """;

    [Test]
    public async Task anchor_map_sums_credits_and_keys_on_final_message() {
        var map = KiroUsage.AnchorMap(Meta);

        await Assert.That(map.Count).IsEqualTo(1);
        await Assert.That(map.ContainsKey("a2")).IsTrue();   // last message_id = final assistant response
        await Assert.That(map["a2"].Credits).IsEqualTo(0.75);
        await Assert.That(map["a2"].ContextPct).IsEqualTo(5.2612);
    }

    [Test]
    public async Task enrich_line_injects_usage_on_anchor_assistant_line_only() {
        var map = KiroUsage.AnchorMap(Meta);

        var anchor = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"}]}}""";
        var enriched = KiroUsage.EnrichLine(anchor, map);
        await Assert.That(enriched).Contains("_kcap_usage");
        await Assert.That(enriched).Contains("0.75");
        await Assert.That(enriched).Contains("5.2612");

        // A non-anchor assistant line is untouched.
        var other = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"x"}]}}""";
        await Assert.That(KiroUsage.EnrichLine(other, map)).DoesNotContain("_kcap_usage");

        // Only AssistantMessage lines are enriched — a Prompt with the anchor id is left alone.
        var prompt = """{"version":"v1","kind":"Prompt","data":{"message_id":"a2","content":[]}}""";
        await Assert.That(KiroUsage.EnrichLine(prompt, map)).DoesNotContain("_kcap_usage");
    }

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"session_state":{"conversation_metadata":{"user_turn_metadatas":[]}}}""")]
    public async Task anchor_map_empty_on_missing_or_malformed(string? json) {
        await Assert.That(KiroUsage.AnchorMap(json).Count).IsEqualTo(0);
    }
}
