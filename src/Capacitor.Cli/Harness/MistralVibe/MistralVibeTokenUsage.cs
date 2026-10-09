using System.Text.Json;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>Vibe's token counts. Its <c>inputTokens</c> includes the cached ones.</summary>
internal sealed record MistralVibeTokenUsage(long InputTokens, long OutputTokens, long CachedInputTokens) {
    public static readonly MistralVibeTokenUsage Zero = new(0, 0, 0);

    public bool Exceeds(MistralVibeTokenUsage other) =>
        InputTokens > other.InputTokens || OutputTokens > other.OutputTokens || CachedInputTokens > other.CachedInputTokens;

    public MistralVibeTokenUsage Minus(MistralVibeTokenUsage other) =>
        new(Math.Max(0, InputTokens - other.InputTokens), Math.Max(0, OutputTokens - other.OutputTokens),
            Math.Max(0, CachedInputTokens - other.CachedInputTokens));

    public static MistralVibeTokenUsage? From(JsonElement? usage) =>
        usage is { ValueKind: JsonValueKind.Object } u
            ? new(Count(u, "inputTokens"), Count(u, "outputTokens"), Count(u, "cachedInputTokens"))
            : null;

    static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    public void WriteTo(Utf8JsonWriter writer) {
        writer.WriteNumber("inputTokens", InputTokens);
        writer.WriteNumber("outputTokens", OutputTokens);
        writer.WriteNumber("cachedInputTokens", CachedInputTokens);
    }
}
