using System.Buffers;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// Reads Mistral Vibe's unified-harness session store — a <c>unified/&lt;session-id&gt;/</c> directory
/// (<c>store_format = mistral.vibe.unified-session-store/v1</c>) — and yields its finished
/// transcript entries as one ordered line stream for
/// <see cref="Capacitor.Models.Transcripts.Harness.MistralVibe.MistralVibeTranscriptEvents"/>.
///
/// <para>The history is folded the way Vibe restores it: <c>CURRENT</c> names a generation whose
/// manifest points at a projection snapshot (inline under <c>snapshot.history.entries</c>, or as an
/// ordered list of pooled <c>chunks/&lt;sha&gt;.json</c>), and exactly one journal segment whose
/// <c>projection_delta</c> records replay on top of it. The chunk pool cannot be read on its own: it
/// also holds the model's checkpoint transcript, the capability catalog, and chunks of older
/// generations.</para>
///
/// <para>An entry is appended while it is still generating and rewritten by <c>replace_entry</c>
/// until its <c>generationStatus</c> is <c>completed</c>, after which Vibe freezes it. Only completed
/// entries are yielded: the projection keys records on the entry id, so an in-progress version
/// recorded first would shadow the finished one.</para>
/// </summary>
internal static class MistralVibeUnifiedStore {
    static readonly HashSet<string> TranscriptEntryTypes = new(StringComparer.Ordinal) { "message", "reasoning", "effect" };

    /// <summary>Whether <paramref name="dir"/> is a unified session directory rather than a legacy
    /// <c>session_*</c> folder or a file.</summary>
    public static bool IsUnifiedSession(string dir) =>
        File.Exists(Path.Combine(dir, "CURRENT"))
     || Directory.Exists(Path.Combine(dir, "generations"))
     || Directory.Exists(Path.Combine(dir, "journal"));

    /// <summary>The finished transcript entries of one unified session, in history order.</summary>
    public static IReadOnlyList<string> ReadLines(string sessionDir) =>
        [.. Fold(sessionDir).Where(e => e is { IsTranscript: true, Completed: true }).Select(e => e.Raw)];

    readonly record struct HistoryEntry(string? Id, string Raw, bool IsTranscript, bool Completed);

    static HistoryEntry Capture(JsonElement entry) => new(
        entry.Str("id"),
        OneLine(entry),
        entry.Str("type") is { } type && TranscriptEntryTypes.Contains(type),
        entry.Str("generationStatus") is null or "completed");

    /// <summary>Each entry becomes one JSONL line, so whitespace from a document written indented
    /// must not survive into it.</summary>
    static string OneLine(JsonElement entry) {
        var raw = entry.GetRawText();
        if (raw.AsSpan().IndexOfAny('\n', '\r') < 0) return raw;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) entry.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static List<HistoryEntry> Fold(string sessionDir) {
        var (entries, segments) = Snapshot(sessionDir);
        foreach (var (path, firstSequence) in segments) Replay(path, firstSequence, entries);
        return entries;
    }

    /// <summary>The published generation's history and the journal segment that continues it. A
    /// session with no readable generation yet replays every segment from an empty history.</summary>
    static (List<HistoryEntry> Entries, IReadOnlyList<(string Path, long FirstSequence)> Segments) Snapshot(string sessionDir) {
        if (ReadJson(Path.Combine(sessionDir, "CURRENT")) is { } current) {
            using (current)
                if (current.RootElement.Str("generation") is { Length: > 0 } generation && generation.All(char.IsAsciiDigit)
                 && Generation(sessionDir, Path.Combine(sessionDir, "generations", generation)) is { } published)
                    return published;
        }

        var journalDir = Path.Combine(sessionDir, "journal");
        var segments   = Directory.Exists(journalDir)
            ? Directory.GetFiles(journalDir, "*.jsonl").OrderBy(JournalSeq).Select(f => (f, 0L)).ToList()
            : [];
        return ([], segments);
    }

    static (List<HistoryEntry>, IReadOnlyList<(string, long)>)? Generation(string sessionDir, string generationDir) {
        using var manifest = ReadJson(Path.Combine(generationDir, "manifest.json"));
        if (manifest?.RootElement.Obj("projection_state") is not { } projection) return null;
        if (FileName(projection.Str("path")) is not { } projectionFile) return null;

        var entries = new List<HistoryEntry>();
        if (projection.Arr("chunks") is { } chunks) {
            foreach (var digest in chunks.EnumerateArray()) {
                if (digest.ValueKind != JsonValueKind.String || FileName(digest.GetString()) is not { } name) return null;
                using var chunk = ReadJson(Path.Combine(sessionDir, "chunks", name + ".json"));
                if (chunk?.RootElement is not { ValueKind: JsonValueKind.Array } items) return null;
                entries.AddRange(items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(Capture));
            }
        } else {
            using var state = ReadJson(Path.Combine(generationDir, projectionFile));
            if (state?.RootElement.Obj("snapshot")?.Obj("history")?.Arr("entries") is { } inline)
                entries.AddRange(inline.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(Capture));
        }

        var segment = manifest.RootElement.Obj("recovery_journal_segment");
        var segments = new List<(string, long)>();
        if (segment?.Str("path") is { } relative && relative.StartsWith("journal/", StringComparison.Ordinal)
         && FileName(relative["journal/".Length..]) is { } journalFile) {
            var first = segment.Value.TryGetProperty("first_sequence", out var f) && f.TryGetInt64(out var n) ? n : 0;
            segments.Add((Path.Combine(sessionDir, "journal", journalFile), first));
        }

        return (entries, segments);
    }

    /// <summary>Applies one segment's <c>projection_delta</c> ops. A torn final line (Vibe still
    /// appending) or an op naming an absent entry is skipped rather than failing the read.</summary>
    static void Replay(string segmentPath, long firstSequence, List<HistoryEntry> entries) {
        IEnumerable<string> lines;
        try { lines = File.ReadLinesShared(segmentPath).ToList(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }

        foreach (var line in lines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument record;
            try { record = JsonDocument.Parse(line); } catch (JsonException) { continue; }

            using (record) {
                var root = record.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.Str("type") != "projection_delta") continue;
                if (root.TryGetProperty("sequence", out var s) && s.TryGetInt64(out var sequence) && sequence < firstSequence) continue;
                if (root.Obj("payload")?.Arr("delta") is not { } delta) continue;

                foreach (var op in delta.EnumerateArray()) Apply(op, entries);
            }
        }
    }

    static void Apply(JsonElement op, List<HistoryEntry> entries) {
        switch (op.Str("op")) {
            case "append_entry" when op.Obj("entry") is { } entry:
                entries.Add(Capture(entry));
                break;
            case "replace_entry" when op.Str("id") is { } id && op.Obj("entry") is { } entry:
                var index = entries.FindIndex(e => e.Id == id);
                if (index >= 0) entries[index] = Capture(entry);
                break;
            case "remove_entry" when op.Str("id") is { } id:
                entries.RemoveAll(e => e.Id == id);
                break;
            case "set_history_entries" when op.Arr("entries") is { } all:
                entries.Clear();
                entries.AddRange(all.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(Capture));
                break;
        }
    }

    /// <summary>The working directory Vibe recorded for the session, from <c>meta.json</c> — nested
    /// under <c>environment.working_directory</c>, with <c>origin_directory</c> as the fallback. Null
    /// when the file is missing/unreadable or names none.</summary>
    public static string? ReadCwd(string sessionDir) {
        using var meta = ReadJson(Path.Combine(sessionDir, "meta.json"));
        return meta?.RootElement is { ValueKind: JsonValueKind.Object } root
            ? root.Obj("environment")?.Str("working_directory") ?? root.Str("origin_directory")
            : null;
    }

    static JsonDocument? ReadJson(string path) {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllTextShared(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>A store-relative name is trusted only as a single path segment.</summary>
    static string? FileName(string? name) =>
        string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 ? null : name;

    static long JournalSeq(string file) =>
        long.TryParse(Path.GetFileNameWithoutExtension(file), out var seq) ? seq : long.MaxValue;
}
