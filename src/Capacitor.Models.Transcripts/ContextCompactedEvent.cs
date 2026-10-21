using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts;

/// The point where an agent compacted its context in place, persisted as `ContextCompacted`.
/// ReplacementHistory is a clone, never an element of the line's own document.
public sealed record ContextCompactedEvent {
    /// The post-compaction preamble verbatim while it fits CompactionHistoryBound.MaxBytes; over it,
    /// only its `compaction` entries, or nothing, with the three fields below recording the cut.
    [JsonPropertyName("replacement_history")]
    public JsonElement? ReplacementHistory { get; init; }

    /// The agent's opaque encrypted summary; nothing here can decrypt it.
    [JsonPropertyName("encrypted_content")]
    public string? EncryptedContent { get; init; }

    [JsonPropertyName("replacement_history_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReplacementHistoryTruncated { get; init; }

    [JsonPropertyName("replacement_history_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReplacementHistoryBytes { get; init; }

    [JsonPropertyName("replacement_history_entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReplacementHistoryEntries { get; init; }
}
