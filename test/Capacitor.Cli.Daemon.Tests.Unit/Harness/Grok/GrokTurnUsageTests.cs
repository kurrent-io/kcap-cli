using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Grok;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

/// <summary>
/// A hosted Grok turn's token usage, which Grok reports only in its own <c>_x.ai/session_notification</c>
/// <c>turn_completed</c> frame, shaped as Grok 1.0.52 sends it (<c>docs/probes/2026-10-13-grok-acp/</c>).
/// </summary>
public class GrokTurnUsageTests {
    static readonly JsonElement EndTurnResult = JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();

    const string ModelUsage = """
        {"grok-4.5-build":{"inputTokens":44757,"outputTokens":139,"totalTokens":44896,"cachedReadTokens":21632,
                           "cacheCreationTokens":512,"reasoningTokens":41,"modelCalls":2},
         "grok-4.6-build":{"inputTokens":1000,"outputTokens":20,"cachedReadTokens":0,"reasoningTokens":5}}
        """;

    static JsonElement Frame(string method = "_x.ai/session_notification", string sessionId = FakeAcpAgent.FixedSessionId,
                             string sessionUpdate = "turn_completed", string modelUsage = ModelUsage) =>
        JsonDocument.Parse($$"""
            {"jsonrpc":"2.0","method":"{{method}}","params":{"sessionId":"{{sessionId}}",
             "update":{"sessionUpdate":"{{sessionUpdate}}","prompt_id":"p-1","stop_reason":"end_turn",
                       "usage":{"inputTokens":45757,"outputTokens":159,"modelUsage":{{modelUsage}} } } } }
            """.ReplaceLineEndings("")).RootElement.Clone();

    static AcpNotification Notification(JsonElement frame) =>
        new(frame.GetProperty("method").GetString()!, frame.GetProperty("params").Clone());

    [Test]
    public async Task Each_model_in_the_turn_reports_its_own_usage() {
        var usage = GrokTurnUsage.Read(Notification(Frame()));

        await Assert.That(usage.Count).IsEqualTo(2);

        var first = usage.Single(u => u.Model == "grok-4.5-build");
        await Assert.That(first.Kind).IsEqualTo(AcpEventKind.TokenUsage);
        await Assert.That(first.UsageInputTokens).IsEqualTo(44757);
        await Assert.That(first.UsageCachedInputTokens).IsEqualTo(21632);
        await Assert.That(first.UsageCacheWriteInputTokens).IsEqualTo(512);
        await Assert.That(first.UsageOutputTokens).IsEqualTo(139);
        await Assert.That(first.UsageReasoningTokens).IsEqualTo(41);

        await Assert.That(usage.Single(u => u.Model == "grok-4.6-build").UsageInputTokens).IsEqualTo(1000);
    }

    /// <summary><c>session/load</c> replays a finished turn's usage under <c>_x.ai/session/update</c>;
    /// counting it would bill that turn twice.</summary>
    [Test]
    public async Task A_replayed_turn_reports_nothing() {
        await Assert.That(GrokTurnUsage.Read(Notification(Frame(method: "_x.ai/session/update")))).IsEmpty();
    }

    [Test]
    [Arguments("response_completed")]
    [Arguments("pending_interaction")]
    public async Task Other_notifications_report_nothing(string sessionUpdate) {
        await Assert.That(GrokTurnUsage.Read(Notification(Frame(sessionUpdate: sessionUpdate)))).IsEmpty();
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("""{"grok-4.5-build":7}""")]
    public async Task Malformed_usage_reports_nothing(string modelUsage) {
        await Assert.That(GrokTurnUsage.Read(Notification(Frame(modelUsage: modelUsage)))).IsEmpty();
    }

    [Test]
    public async Task The_runtime_emits_the_usage_of_its_own_session() {
        await using var h = new AcpRuntimeHarness(GrokTurnUsage.Read);
        h.Fake.EnqueuePromptScript([Frame()], EndTurnResult);

        await h.StartAsync();

        var usage = await h.ReadUntilAsync(e => e.Kind == AcpEventKind.TokenUsage);
        await Assert.That(usage.Model).IsEqualTo("grok-4.5-build");
        await Assert.That(usage.TimestampIso).IsNotNull();
        await Assert.That((await h.ReadUntilAsync(e => e.Kind == AcpEventKind.TokenUsage)).Model).IsEqualTo("grok-4.6-build");
    }

    /// <summary>A frame for another session on the same connection is not this agent's spend. The marker
    /// after it proves the frame was read, so the absence is not just a slow wire.</summary>
    [Test]
    public async Task The_runtime_ignores_another_sessions_usage() {
        await using var h = new AcpRuntimeHarness(GrokTurnUsage.Read);
        var marker = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("done"));
        h.Fake.EnqueuePromptScript([Frame(sessionId: "some-other-session"), marker], EndTurnResult);

        await h.StartAsync();

        var seen = new List<AcpEventEnvelope>();
        await h.ReadUntilAsync(e => { seen.Add(e); return e.Kind == AcpEventKind.AssistantText; });
        await Assert.That(seen.Any(e => e.Kind == AcpEventKind.TokenUsage)).IsFalse();
    }

    [Test]
    public async Task The_grok_row_reads_turn_usage() {
        await Assert.That(AcpVendorDescriptors.Grok.TurnUsageReader).IsNotNull();
        await Assert.That(AcpVendorDescriptors.OpenCode.TurnUsageReader).IsNull();
    }
}
