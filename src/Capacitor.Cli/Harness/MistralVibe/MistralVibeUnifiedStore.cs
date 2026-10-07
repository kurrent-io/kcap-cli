using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// Reads Mistral Vibe's unified-harness session store — a <c>unified/&lt;session-id&gt;/</c> directory
/// (<c>store_format = mistral.vibe.unified-session-store/v1</c>) and flattens its public-session-state
/// entries into one ordered line stream that
/// <see cref="Capacitor.Models.Transcripts.Harness.MistralVibe.MistralVibeTranscriptEvents"/> projects.
///
/// <para>The store is event-sourced. Published history is pooled in content-addressed
/// <c>chunks/&lt;sha&gt;.json</c> arrays; turns since the current generation live in
/// <c>journal/&lt;seq&gt;.jsonl</c> as <c>projection_delta</c> records, and each new entry rides inside an
/// <c>append_entry</c> op (<c>payload.delta[].entry</c>). A transcript entry carries a <c>type</c>
/// (<c>message</c>/<c>reasoning</c>/<c>effect</c>), a <c>role</c>, <c>content</c>, a numeric epoch-ms
/// <c>createdAt</c>, and a stable <c>id</c> — the id is what dedups an entry that appears both as a
/// journal delta and, once the generation is published, in a chunk. Non-transcript records
/// (<c>core_input</c>/<c>action_intent</c>/<c>set_envelope</c>, config entries, <c>notice</c>,
/// <c>checkpoint</c>) are skipped. Reads are defensive: an unreadable chunk or line is dropped, never
/// fatal.</para>
/// </summary>
internal static class MistralVibeUnifiedStore {
    static readonly HashSet<string> TranscriptEntryTypes = new(StringComparer.Ordinal) { "message", "reasoning", "effect" };

    /// <summary>Whether <paramref name="dir"/> looks like a unified session directory rather than a
    /// legacy <c>session_*</c> folder.</summary>
    public static bool IsUnifiedSession(string dir) =>
        Directory.Exists(Path.Combine(dir, "chunks"))
     || Directory.Exists(Path.Combine(dir, "generations"))
     || Directory.Exists(Path.Combine(dir, "journal"));

    /// <summary>The flattened, ordered entry lines for one unified session directory.</summary>
    public static IReadOnlyList<string> ReadLines(string sessionDir) {
        var entries = new List<(double? At, long Order, string Raw)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        long order  = 0;

        void Consider(JsonElement entry) {
            if (entry.ValueKind != JsonValueKind.Object) return;
            if (entry.Str("type") is not { } type || !TranscriptEntryTypes.Contains(type)) return;
            // An entry pooled into a chunk after its generation is published is the same entry that
            // first arrived as a journal delta — dedup by its stable id so it projects once.
            if (entry.Str("id") is { } id && !seenIds.Add(id)) return;
            entries.Add((CreatedAtMs(entry), order++, entry.GetRawText()));
        }

        var chunksDir = Path.Combine(sessionDir, "chunks");
        if (Directory.Exists(chunksDir)) {
            foreach (var file in Directory.GetFiles(chunksDir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)) {
                if (!TryParse(file, out var doc)) continue;
                using (doc)
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var element in doc.RootElement.EnumerateArray()) Consider(element);
            }
        }

        var journalDir = Path.Combine(sessionDir, "journal");
        if (Directory.Exists(journalDir)) {
            foreach (var file in Directory.GetFiles(journalDir, "*.jsonl").OrderBy(JournalSeq)) {
                IEnumerable<string> lines;
                try { lines = File.ReadLinesShared(file); } catch { continue; }
                foreach (var line in lines) {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch { continue; }
                    using (doc) {
                        if (doc.RootElement.Str("type") != "projection_delta") continue;
                        if (doc.RootElement.Obj("payload")?.Arr("delta") is not { } delta) continue;
                        foreach (var op in delta.EnumerateArray())
                            if (op.Str("op") == "append_entry" && op.Obj("entry") is { } entry) Consider(entry);
                    }
                }
            }
        }

        var ordered = entries.All(e => e.At is not null)
            ? entries.OrderBy(e => e.At!.Value).ThenBy(e => e.Order)
            : entries.OrderBy(e => e.Order);
        return ordered.Select(e => e.Raw).ToList();
    }

    /// <summary>The working directory Vibe recorded for the session, from <c>meta.json</c> — nested
    /// under <c>environment.working_directory</c>, with <c>origin_directory</c> as the fallback. Null
    /// when the file is missing/unreadable or names none.</summary>
    public static string? ReadCwd(string sessionDir) {
        var metaPath = Path.Combine(sessionDir, "meta.json");
        if (!File.Exists(metaPath)) return null;
        try {
            using var doc = JsonDocument.Parse(File.ReadAllTextShared(metaPath));
            var root = doc.RootElement;
            return root.Obj("environment")?.Str("working_directory") ?? root.Str("origin_directory");
        } catch {
            return null;
        }
    }

    /// <summary><c>createdAt</c> as epoch milliseconds, parsed from the numeric form Vibe writes, or an
    /// ISO-8601 string if a future version switches — either way a sortable key. Null when absent.</summary>
    static double? CreatedAtMs(JsonElement entry) {
        if (!(entry.TryGetProperty("createdAt", out var c)
           || entry.TryGetProperty("created_at", out c)
           || entry.TryGetProperty("timestamp", out c))) return null;

        if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var ms)) return ms;
        if (c.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(c.GetString(), out var ts))
            return ts.ToUnixTimeMilliseconds();
        return null;
    }

    static bool TryParse(string file, out JsonDocument doc) {
        try { doc = JsonDocument.Parse(File.ReadAllTextShared(file)); return true; }
        catch { doc = null!; return false; }
    }

    static int JournalSeq(string file) {
        var name = Path.GetFileNameWithoutExtension(file);
        return int.TryParse(name, out var seq) ? seq : int.MaxValue;
    }
}
