using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// One already-persisted cluster member a usage backfill names; the tool name lets a read model
/// credit the tool's tokens without counting another call.
public sealed record CodexUsageBackfillTarget {
    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("is_echo")]
    public bool IsEcho { get; init; }
}
