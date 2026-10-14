using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Reads a finished Grok turn's token usage, one envelope per model, from the <c>turn_completed</c>
/// frame Grok sends outside <c>session/update</c>. <c>inputTokens</c> is gross, cached reads included.
/// </summary>
internal static class GrokTurnUsage {
    // session/load replays a finished turn's frame under _x.ai/session/update instead.
    const string LiveMethod = "_x.ai/session_notification";

    internal static IReadOnlyList<AcpEventEnvelope> Read(AcpNotification notification) {
        if (notification.Method != LiveMethod
         || notification.Params?.Obj("update") is not { } update
         || update.Str("sessionUpdate") != "turn_completed"
         || update.Obj("usage")?.Obj("modelUsage") is not { } byModel)
            return [];

        List<AcpEventEnvelope>? usage = null;
        foreach (var model in byModel.EnumerateObject()) {
            if (!model.Value.IsObject || model.Name.Length == 0) continue;

            (usage ??= []).Add(new AcpEventEnvelope(
                Kind:                       AcpEventKind.TokenUsage,
                Model:                      model.Name,
                UsageInputTokens:           model.Value.Num("inputTokens"),
                UsageCachedInputTokens:     model.Value.Num("cachedReadTokens"),
                UsageCacheWriteInputTokens: model.Value.Num("cacheCreationTokens"),
                UsageOutputTokens:          model.Value.Num("outputTokens"),
                UsageReasoningTokens:       model.Value.Num("reasoningTokens")));
        }

        return usage ?? [];
    }
}
