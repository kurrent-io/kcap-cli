using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The one-shot fit test over the wire. An event-count lower bound decides without a read when the scope cannot fit;
/// otherwise each available source's events stream in manifest order, every canonical body is completed, and assembly stops
/// on the first entry past the limit or the first body that is not UTF-8 text. A successful read that does not parse, or whose
/// revisions or body offsets do not move forward inside the requested range, fails assembly under its own status rather than
/// being read again. The writer escapes every non-ASCII character, so the trace's characters and bytes are one number.</summary>
public sealed class EvidenceTraceAssembler(EvidenceReadClient reader, string token, int pageBudgetBytes) {
    public const int MinEntryChars = 32;
    const int BodyChunkBytes = 65_536;
    const int NoFit = -1;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool CannotFit(IEnumerable<EvidenceSourceDto> sources, int limitChars) {
        long lower = 0;
        foreach (var s in sources.Where(s => s.IsAvailable)) lower += (s.RevisionCutoff - s.FirstRevision + 1) * MinEntryChars;
        return lower > limitChars;
    }

    public async Task<EvidenceTraceResult> AssembleAsync(IReadOnlyList<EvidenceSourceDto> sources, int limitChars, CancellationToken ct) {
        if (CannotFit(sources, limitChars)) return EvidenceTraceResult.TooLarge((long)limitChars + MinEntryChars, 0);

        var cites  = new Dictionary<string, string>(StringComparer.Ordinal);
        var detail = new List<(string Source, long Revision, int Offset, int Length)>();
        var reads  = 0;
        var k      = 0;

        using var buffer = new MemoryStream();
        using var w      = new Utf8JsonWriter(buffer);
        w.WriteStartArray();
        foreach (var source in sources.Where(s => s.IsAvailable)) {
            w.WriteStartObject();
            w.WriteString("source", source.SourceId);
            w.WriteStartArray("entries");
            var written = 0;
            for (var from = source.FirstRevision; from <= source.RevisionCutoff;) {
                reads++;
                var page = await reader.GetAsync("evidence-events",
                    [("token", token), ("ref", $"{source.SourceId}@{from}-{source.RevisionCutoff}"), ("budget_bytes", pageBudgetBytes.ToString(Inv))], ct);
                if (!page.IsSuccess) return EvidenceTraceResult.Failed(page.Status, reads);

                using var doc = TryParse(page.Body);
                if (doc is null) return EvidenceTraceResult.Failed(page.Status, reads);
                var more = doc.RootElement.Str("next_cursor") is not null;
                if (doc.RootElement.Arr("entries") is not { } entries) {
                    if (more) return EvidenceTraceResult.Failed(page.Status, reads);
                    break;
                }
                long? last = null;
                foreach (var entry in entries.EnumerateArray()) {
                    if (entry.Num("revision") is not { } revision || revision < from || revision > source.RevisionCutoff || revision <= last)
                        return EvidenceTraceResult.Failed(page.Status, reads);
                    var cite = JudgeCiteHandles.OneShot(k++);
                    cites[cite] = entry.Str("ref") ?? "";

                    var (bodies, status) = await ReadBodiesAsync(entry, limitChars - Position(w), ct);
                    if (status == NoFit) return EvidenceTraceResult.TooLarge(Position(w) + 1, reads);
                    if (status is { } failed) return EvidenceTraceResult.Failed(failed, reads);
                    if (!ArgumentsParse(bodies)) return EvidenceTraceResult.Failed(page.Status, reads);

                    // Every entry after the first in its array is preceded by one comma.
                    var start = (int)Position(w) + (written++ > 0 ? 1 : 0);
                    WriteEntry(w, entry, cite, bodies);
                    var end = Position(w);
                    if (end > limitChars) return EvidenceTraceResult.TooLarge(end, reads);

                    detail.Add((source.SourceId, revision, start, (int)end - start));
                    last = revision;
                }
                // Advance past the last event returned: the page's to_revision echoes the requested end.
                if (!more) break;
                if (last is null) return EvidenceTraceResult.Failed(page.Status, reads);
                if (last.Value == source.RevisionCutoff) break;
                from = last.Value + 1;
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.Flush();

        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return json.Length <= limitChars
            ? new EvidenceTraceResult(true, json, json.Length, json.Length, cites, detail, reads, null)
            : EvidenceTraceResult.TooLarge(json.Length, reads);
    }

    // Reads each deferred body chunk by chunk, charged by its escaped length against the trace's remaining allowance, so a
    // body larger than the allowance stops one chunk past it rather than being read whole.
    async Task<(List<(string Field, int? Ordinal, string Content)> Bodies, int? Status)> ReadBodiesAsync(JsonElement entry, long allowance, CancellationToken ct) {
        var bodies    = new List<(string Field, int? Ordinal, string Content)>();
        var remaining = allowance;
        foreach (var (bodyRef, field, ordinal) in EvidenceCanonicalContent.Deferred(entry)) {
            var content = new StringBuilder();
            long charged = 2;
            for (long offset = 0; ;) {
                List<(string Key, string Value)> query = [
                    ("token", token), ("ref", bodyRef), ("field", field), ("offset", offset.ToString(Inv)),
                    ("max_bytes", Math.Clamp(remaining + 1, 1, BodyChunkBytes).ToString(Inv))
                ];
                if (ordinal is { } o) query.Add(("ordinal", o.ToString(Inv)));
                var chunk = await reader.GetAsync("evidence-body", query, ct);
                if (!chunk.IsSuccess) return ([], chunk.Status);

                using var doc = TryParse(chunk.Body);
                if (doc is null) return ([], chunk.Status);
                if (doc.RootElement.Str("encoding") != "utf-8") return ([], NoFit);
                var text = doc.RootElement.Str("content") ?? "";
                charged += JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;
                if (charged > remaining) return ([], NoFit);
                content.Append(text);
                if (doc.RootElement.Num("next_offset") is not { } next) break;
                // A continuation must follow content and start past it; anything else would re-read the same chunk.
                if (text.Length == 0 || next <= offset) return ([], chunk.Status);
                offset = next;
            }
            remaining -= charged;
            bodies.Add((field, ordinal, content.ToString()));
        }
        return (bodies, null);
    }

    // A resolved "arguments" body belongs to one call by ordinal, so it is nested there like an inline value
    // rather than written as a sibling of "calls" — the same field reads as structured JSON either way it arrived.
    static void WriteEntry(Utf8JsonWriter w, JsonElement entry, string cite, List<(string Field, int? Ordinal, string Content)> bodies) {
        w.WriteStartObject();
        w.WriteString("ref", entry.Str("ref"));
        w.WriteString("cite", cite);
        w.WriteString("kind", entry.Str("kind"));
        w.WriteString("type", entry.Str("event_type"));
        if (entry.Str("text") is { } text) w.WriteString("text", text);
        if (entry.Str("output") is { } output) w.WriteString("output", output);
        if (entry.Arr("calls") is { } calls) {
            var resolvedArguments = bodies.Where(b => b.Field == "arguments" && b.Ordinal is not null)
                .ToDictionary(b => b.Ordinal!.Value, b => b.Content);
            w.WriteStartArray("calls");
            foreach (var call in calls.EnumerateArray()) {
                w.WriteStartObject();
                var ordinal = (int)(call.Num("ordinal") ?? 0);
                w.WriteNumber("ordinal", ordinal);
                if (call.Str("tool") is { } tool) w.WriteString("tool", tool); else w.WriteNull("tool");
                w.WritePropertyName("arguments");
                if (call.Prop("arguments") is { IsNull: false } arguments) arguments.WriteTo(w);
                else if (resolvedArguments.TryGetValue(ordinal, out var resolved)) {
                    using var argumentsDoc = JsonDocument.Parse(resolved);
                    argumentsDoc.RootElement.WriteTo(w);
                } else w.WriteNullValue();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        foreach (var (field, ordinal, content) in bodies) {
            if (field == "arguments") continue;
            w.WriteString(ordinal is { } n ? $"{field}[{n}]" : field, content);
        }
        w.WriteEndObject();
    }

    static JsonDocument? TryParse(string body) {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    static bool ArgumentsParse(List<(string Field, int? Ordinal, string Content)> bodies) {
        foreach (var (field, _, content) in bodies) {
            if (field != "arguments") continue;
            using var doc = TryParse(content);
            if (doc is null) return false;
        }
        return true;
    }

    static long Position(Utf8JsonWriter w) => w.BytesCommitted + w.BytesPending;
}
