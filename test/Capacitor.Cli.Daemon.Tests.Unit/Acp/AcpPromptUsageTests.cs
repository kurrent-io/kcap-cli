using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The <c>usage</c> on a <c>session/prompt</c> response, in the shapes Gemini, Copilot and OpenCode
/// each sent for one trivial turn.
/// </summary>
public class AcpPromptUsageTests {
    const string Gemini = """
        {"stopReason":"end_turn","usage":{"inputTokens":40762,"outputTokens":1,"thoughtTokens":63,"totalTokens":40763},
         "_meta":{"quota":{"token_count":{"input_tokens":40762,"output_tokens":1},
                  "model_usage":[{"model":"gemini-3.8-flash","token_count":{"input_tokens":40762,"output_tokens":1}}]}}}
        """;

    const string Copilot = """
        {"stopReason":"end_turn","usage":{"inputTokens":65759,"outputTokens":4,"totalTokens":65763,"thoughtTokens":0,
                                          "cachedReadTokens":0,"cachedWriteTokens":65755}}
        """;

    const string OpenCode = """
        {"stopReason":"end_turn","usage":{"inputTokens":38589,"outputTokens":8,"totalTokens":38702,"thoughtTokens":35,
                                          "cachedReadTokens":70},"_meta":{}}
        """;

    static JsonElement Result(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Test]
    public async Task Gemini_names_its_own_model() {
        var usage = AcpPromptUsage.Read(Result(Gemini), sessionModel: "ignored").Single();

        await Assert.That(usage.Kind).IsEqualTo(AcpEventKind.TokenUsage);
        await Assert.That(usage.Model).IsEqualTo("gemini-3.8-flash");
        await Assert.That(usage.UsageInputTokens).IsEqualTo(40762);
        await Assert.That(usage.UsageOutputTokens).IsEqualTo(1);
        await Assert.That(usage.UsageReasoningTokens).IsEqualTo(63);
    }

    [Test]
    public async Task Gemini_reports_each_model_when_a_turn_used_several() {
        const string twoModels = """
            {"usage":{"inputTokens":300,"outputTokens":30,"totalTokens":330},
             "_meta":{"quota":{"model_usage":[{"model":"gemini-3.8-flash","token_count":{"input_tokens":100,"output_tokens":10}},
                                              {"model":"gemini-3.1-pro","token_count":{"input_tokens":200,"output_tokens":20}}]}}}
            """;

        var usage = AcpPromptUsage.Read(Result(twoModels), sessionModel: null);

        await Assert.That(usage.Select(u => $"{u.Model} {u.UsageInputTokens} {u.UsageOutputTokens}").ToList())
            .IsEquivalentTo(["gemini-3.8-flash 100 10", "gemini-3.1-pro 200 20"]);
    }

    /// <summary>Copilot counts the cached tiers inside inputTokens; OpenCode adds them on top. Both arrive gross.</summary>
    [Test]
    [Arguments(Copilot,  65759L, 0L,  65755L)]
    [Arguments(OpenCode, 38659L, 70L, 0L)]
    public async Task Input_is_carried_gross_of_the_cached_tiers(string json, long input, long cacheRead, long cacheWrite) {
        var usage = AcpPromptUsage.Read(Result(json), sessionModel: "claude-sonnet-4.5").Single();

        await Assert.That(usage.Model).IsEqualTo("claude-sonnet-4.5");
        await Assert.That(usage.UsageInputTokens).IsEqualTo(input);
        await Assert.That(usage.UsageCachedInputTokens).IsEqualTo(cacheRead);
        await Assert.That(usage.UsageCacheWriteInputTokens).IsEqualTo(cacheWrite);
    }

    /// <summary>The server drops usage that names no model, so there is nothing to send.</summary>
    [Test]
    [Arguments(Copilot)]
    [Arguments("""{"stopReason":"end_turn"}""")]
    public async Task Without_a_model_or_usage_nothing_is_reported(string json) =>
        await Assert.That(AcpPromptUsage.Read(Result(json), sessionModel: null)).IsEmpty();

    [Test]
    public async Task The_runtime_reports_a_turns_usage_under_the_sessions_model() {
        await using var h = new AcpRuntimeHarness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "claude-sonnet-4.5", [("claude-sonnet-4.5", "Sonnet")]));
        h.Fake.EnqueuePromptScript([], Result(OpenCode));

        await h.StartAsync();

        var usage = await h.ReadUntilAsync(e => e.Kind == AcpEventKind.TokenUsage);
        await Assert.That(usage.Model).IsEqualTo("claude-sonnet-4.5");
        await Assert.That(usage.UsageInputTokens).IsEqualTo(38659);
    }

    /// <summary>A vendor with its own usage reader is counted there, so a usage object on its response is not
    /// read a second time. The marker after the response proves the turn was read.</summary>
    [Test]
    public async Task A_vendor_with_its_own_reader_is_not_counted_twice() {
        await using var h = new AcpRuntimeHarness(GrokTurnUsage.Read);
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, "grok-4.5", [("grok-4.5", "Grok")]));
        h.Fake.EnqueuePromptScript([], Result(OpenCode));

        await h.StartAsync();
        await h.Runtime.SendUserInputAsync("second");

        var seen = new List<AcpEventEnvelope>();
        await h.ReadUntilAsync(e => { seen.Add(e); return e.Kind == AcpEventKind.AssistantText; });
        await Assert.That(seen.Any(e => e.Kind == AcpEventKind.TokenUsage)).IsFalse();
    }
}
