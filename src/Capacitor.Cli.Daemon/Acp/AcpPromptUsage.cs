using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Models.Transcripts;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Reads a turn's token usage from the <c>usage</c> object on a <c>session/prompt</c> response, which
/// Gemini, Copilot and OpenCode send. The response names no model, except Gemini's
/// <c>_meta.quota.model_usage</c>, so the session's resolved model stands in.
/// </summary>
public static class AcpPromptUsage {
    public static IReadOnlyList<AcpEventEnvelope> Read(JsonElement result, string? sessionModel) {
        if (result.Obj("usage") is not { } usage) return [];

        var byModel = result.Obj("_meta")?.Obj("quota")?.Arr("model_usage") is { } list
            ? list.EnumerateArray().Where(m => m.Str("model") is { Length: > 0 }).ToList()
            : [];

        if (byModel.Count > 1)
            return [.. byModel.Select(m => Envelope(m.Str("model")!, m.Obj("token_count")?.Num("input_tokens"), null, null,
                m.Obj("token_count")?.Num("output_tokens"), null))];

        var model = byModel.Count == 1 ? byModel[0].Str("model") : sessionModel;
        if (string.IsNullOrEmpty(model)) return [];

        var input      = usage.Num("inputTokens");
        var output     = usage.Num("outputTokens");
        var cacheRead  = usage.Num("cachedReadTokens") ?? 0;
        var cacheWrite = usage.Num("cachedWriteTokens") ?? 0;

        // Vendors disagree on whether inputTokens includes the cached tiers: OpenCode's totalTokens
        // adds them on top, Copilot's counts them inside. The envelope carries gross input.
        if (input is { } i && cacheRead + cacheWrite > 0
         && usage.Num("totalTokens") is { } total && total >= i + (output ?? 0) + cacheRead + cacheWrite)
            input = i + cacheRead + cacheWrite;

        return [Envelope(model, input, cacheRead, cacheWrite, output, usage.Num("thoughtTokens"))];
    }

    static AcpEventEnvelope Envelope(string model, long? input, long? cacheRead, long? cacheWrite, long? output, long? reasoning) =>
        new(Kind: AcpEventKind.TokenUsage,
            Model: model,
            UsageInputTokens: input,
            UsageCachedInputTokens: cacheRead,
            UsageCacheWriteInputTokens: cacheWrite,
            UsageOutputTokens: output,
            UsageReasoningTokens: reasoning);
}
