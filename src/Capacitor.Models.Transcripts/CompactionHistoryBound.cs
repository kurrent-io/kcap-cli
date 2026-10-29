using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Capacitor.Models.Transcripts;

/// Bounds a compaction's replacement history. Codex re-carries every user message on each
/// compaction, so one event could pass the store's 16 MiB append limit; those messages already
/// exist as their own events, so over the budget only the `compaction` entries are kept. The budget
/// is on the raw JSON, which the store may re-escape larger.
public static class CompactionHistoryBound {
    public const int MaxBytes = 64 * 1024;

    public static ContextCompactedEvent Build(JsonElement? history, string? encryptedContent) {
        if (history is not { } element) return new() { EncryptedContent = encryptedContent };

        var bytes = Encoding.UTF8.GetByteCount(element.GetRawText());
        if (bytes <= MaxBytes) return new() { ReplacementHistory = element.Clone(), EncryptedContent = encryptedContent };

        var isArray = element.IsArray;
        return new() {
            ReplacementHistory          = isArray ? CompactionEntriesWithinBudget(element) : null,
            EncryptedContent            = encryptedContent,
            ReplacementHistoryTruncated = true,
            ReplacementHistoryBytes     = bytes,
            ReplacementHistoryEntries   = isArray ? element.GetArrayLength() : null,
        };
    }

    static JsonElement? CompactionEntriesWithinBudget(JsonElement history) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartArray();
            foreach (var entry in history.EnumerateArray())
                if (entry.Str("type") == "compaction") entry.WriteTo(writer);
            writer.WriteEndArray();
        }

        if (buffer.WrittenCount > MaxBytes) return null;

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
