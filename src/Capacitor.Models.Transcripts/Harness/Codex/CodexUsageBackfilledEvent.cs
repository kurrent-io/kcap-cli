using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// A response cluster's usage restated for members already persisted before their `token_count`
/// arrived; read models treat it as the `$usage` the cluster's canonical member would have carried.
/// Persisted as `CodexUsageBackfilled`. The leaf never emits it: the caller builds it from a
/// UsageApplied whose targets it no longer holds.
public sealed record CodexUsageBackfilledEvent {
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public long? ReasoningTokens { get; init; }

    [JsonPropertyName("model_context_window")]
    public long? ModelContextWindow { get; init; }

    /// The context occupancy when the usage sums several model calls; absent on Codex's own events,
    /// where input plus cache read is one call's occupancy.
    [JsonPropertyName("occupancy_tokens")]
    public long? OccupancyTokens { get; init; }

    [JsonPropertyName("targets")]
    public List<CodexUsageBackfillTarget> Targets { get; init => field = value ?? []; } = [];

    [JsonIgnore]
    public long PeakCandidateTokens => OccupancyTokens ?? InputTokens + CacheReadTokens;
}
