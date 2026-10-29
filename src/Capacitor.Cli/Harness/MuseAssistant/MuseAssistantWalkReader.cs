using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Reads one walk directory and refuses anything the cron could have produced wrongly: a
/// cursor chain Muse did not produce, a page missing or unlisted, an item the server could not record.
/// Nothing is read as data until everything has been checked, so a changed Muse shape stops recording
/// instead of recording gaps.</summary>
public static class MuseAssistantWalkReader {
    /// The server never advances a line high-water mark for a line number at or above this.
    const long MaxTrackableSeq = 1_000_000_000;

    // Lifecycle posts render the time as ISO 8601; a value past these bounds would throw there, after
    // a pending start had been saved, and every replay would hit it again.
    static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static string? ReadChatId(string dir) {
        var path = Path.Join(dir, "manifest.json");
        if (!File.Exists(path)) return null;

        try {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.Str("chat_id") is { Length: > 0 } id ? id : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
            return null;
        }
    }

    public static (MuseAssistantWalk? Walk, string? Problem) Load(string dir) {
        try {
            return LoadCore(dir);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return (null, $"{dir} is unreadable: {e.Message}");
        }
    }

    static (MuseAssistantWalk? Walk, string? Problem) LoadCore(string dir) {
        if (!Directory.Exists(dir)) return (null, $"{dir} does not exist");

        MuseAssistantManifest? manifest;
        try {
            var bytes = File.ReadAllBytes(Path.Join(dir, "manifest.json"));
            if (!IsUtf8(bytes)) return (null, "manifest.json is not valid UTF-8");

            manifest = JsonSerializer.Deserialize(bytes, MuseAssistantJsonContext.Default.MuseAssistantManifest);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
            return (null, $"manifest.json is unreadable: {e.Message}");
        }

        if (manifest?.ChatId is not { Length: > 0 } chatId) return (null, "manifest.json has no chat_id");
        if (manifest.IsMain is not { } isMain) return (null, "manifest.json is_main must be a boolean");
        if (manifest.FetchedAtMs is not { } fetched || fetched <= 0) return (null, "manifest.json fetched_at_ms must be a positive integer");
        if (manifest.Pages is not { Length: > 0 } pages) return (null, "manifest.json lists no pages");

        var listed = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };

        for (var i = 0; i < pages.Length; i++) {
            var expected = $"page-{i:000}.json";
            if (pages[i]?.File != expected) return (null, $"pages[{i}] must be {expected}, found {pages[i]?.File ?? "(none)"}");

            listed.Add(expected);
            if (!File.Exists(Path.Join(dir, expected))) return (null, $"{expected} is listed but missing");
        }

        var present = Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.Ordinal);
        if (!present.SetEquals(listed))
            return (null, $"files not listed in manifest.json: {string.Join(", ", present.Except(listed).Order(StringComparer.Ordinal))}");

        if (pages[0]!.Cursor is not null) return (null, "page-000 must be fetched without a cursor");

        var items           = new List<MuseAssistantItem>();
        var pageMin         = new long?[pages.Length];
        var pageMax         = new long?[pages.Length];
        var previousNext    = (string?)null;
        var previousHasMore = true;
        var oldestHasMore   = false;

        for (var i = 0; i < pages.Length; i++) {
            var file = pages[i]!.File!;
            JsonDocument doc;

            try {
                var bytes = File.ReadAllBytes(Path.Join(dir, file));
                if (!IsUtf8(bytes)) return (null, $"{file} is not valid UTF-8");

                doc = JsonDocument.Parse(bytes);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
                return (null, $"{file} is unreadable: {e.Message}");
            }

            using (doc) {
                try {
                    var root = doc.RootElement;
                    if (!root.IsObject) return (null, $"{file} is not an object");
                    if (root.Str("chat_id") != chatId) return (null, $"{file} chat_id differs from the manifest");
                    if (root.Obj("page") is not { } page) return (null, $"{file} has no page object");
                    if (root.Arr("turns") is not { } turns) return (null, $"{file} has no turns array");
                    if (page.Bool("has_more") is not { } hasMore) return (null, $"{file} page.has_more must be a boolean");

                    if (i > 0) {
                        if (!previousHasMore) return (null, $"page-{i - 1:000} has has_more:false but {file} follows it");
                        if (previousNext is null || pages[i]!.Cursor != previousNext) return (null, $"{file} was not fetched with page-{i - 1:000}'s next_cursor");
                    }

                    foreach (var turn in turns.EnumerateArray()) {
                        if (!turn.IsObject || turn.Str("id") is not { Length: > 0 } turnId) return (null, $"{file} has a turn without an id");
                        if (turn.Arr("items") is not { } turnItems) return (null, $"{file} turn {turnId} has no items array");

                        foreach (var item in turnItems.EnumerateArray()) {
                            if (!item.IsObject) return (null, $"{file} turn {turnId} has an item that is not an object");
                            if (item.Str("role") is not { Length: > 0 } role) return (null, $"{file} turn {turnId}: an item has no role");
                            if (item.Num("seq") is not { } seq || seq < 0)
                                return (null, $"{file} turn {turnId}: seq must be a non-negative integer");
                            if (seq >= MaxTrackableSeq)
                                return (null, $"{file} turn {turnId}: seq {seq} is at or above {MaxTrackableSeq}, which the server cannot track");
                            if (item.Num("occurred_at_ms") is not { } at) return (null, $"{file} turn {turnId} seq {seq}: occurred_at_ms must be an integer");
                            if (at < MinUnixMs || at > MaxUnixMs) return (null, $"{file} turn {turnId} seq {seq}: occurred_at_ms {at} is outside the representable time range");

                            if (item.Prop("chat_id") is not null || item.Prop("turn_id") is not null)
                                return (null, $"{file} turn {turnId} seq {seq}: an item must not carry its own chat_id or turn_id");

                            var content = item.Str("content");
                            if (MuseAssistantRoles.IsSelected(role) && string.IsNullOrEmpty(content))
                                return (null, $"{file} turn {turnId} seq {seq}: a {role} item has no non-empty string content");

                            items.Add(new(turnId, role, seq, at, content, Line(chatId, turnId, item)));
                            pageMin[i] = pageMin[i] is { } mn ? Math.Min(mn, seq) : seq;
                            pageMax[i] = pageMax[i] is { } mx ? Math.Max(mx, seq) : seq;
                        }
                    }

                    previousNext    = page.Str("next_cursor");
                    previousHasMore = hasMore;
                    oldestHasMore   = hasMore;
                } catch (InvalidOperationException e) {
                    return (null, $"{file} holds a string that is not valid UTF-16: {e.Message}");
                }
            }
        }

        items.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        for (var i = 1; i < items.Count; i++) {
            if (items[i].Seq == items[i - 1].Seq) return (null, $"seq {items[i].Seq} appears twice");
            if (items[i].OccurredAtMs < items[i - 1].OccurredAtMs) return (null, $"seq {items[i].Seq} is earlier than seq {items[i - 1].Seq}");
        }

        long? olderMax = null;
        for (var i = pages.Length - 1; i >= 0; i--) {
            if (pageMin[i] is { } newer && olderMax is { } older && newer <= older)
                return (null, $"page-{i:000} and an older page overlap or are out of walk order");

            if (pageMax[i] is { } top) olderMax = olderMax is { } m ? Math.Max(m, top) : top;
        }

        return (new MuseAssistantWalk(chatId, isMain, fetched, items, oldestHasMore, pageMin[^1]), null);
    }

    /// <summary>The item with <c>chat_id</c> and <c>turn_id</c> spliced in front of its first member.
    /// Textual, so no serializer reshapes the item and nothing needs runtime type metadata; the one
    /// rewrite is dropping structural line breaks, because a transcript line must be a single line.</summary>
    public static string Line(string chatId, string turnId, JsonElement item) {
        var raw = item.GetRawText();
        if (raw.AsSpan().ContainsAny('\n', '\r')) raw = Minify(item);

        return $"{{\"chat_id\":\"{JsonEncodedText.Encode(chatId)}\",\"turn_id\":\"{JsonEncodedText.Encode(turnId)}\",{raw[1..]}";
    }

    static bool IsUtf8(byte[] bytes) {
        try {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return true;
        } catch (DecoderFallbackException) {
            return false;
        }
    }

    static string Minify(JsonElement item) {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            item.WriteTo(writer);

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
