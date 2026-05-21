using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Core.Cursor;

public static class CursorPayloadAssembler {
    public const int BubbleTextCapBytes  = 256 * 1024;
    public const int BlobSoftCapBytes    = 256 * 1024;
    public const int PayloadHardCapBytes = 10 * 1024 * 1024;

    public static CursorBubble AssembleBubble(string rawJson, string repoPath) {
        using var doc = JsonDocument.Parse(rawJson);
        var r = doc.RootElement;

        string? bubbleId = r.TryGetProperty("bubbleId", out var bidEl) ? bidEl.GetString() : "unknown";
        string? text = r.TryGetProperty("text", out var t) ? GetStringOrSerialize(t) : null;
        if (text is { Length: > 0 } && Encoding.UTF8.GetByteCount(text) > BubbleTextCapBytes)
            text = text[..(BubbleTextCapBytes / 4)]; // crude cap; UTF-8 safe-enough

        CursorToolFormerData? tfd = null;
        if (r.TryGetProperty("toolFormerData", out var tfdEl) && tfdEl.ValueKind == JsonValueKind.Object) {
            tfd = new CursorToolFormerData {
                ToolCallId = tfdEl.TryGetProperty("toolCallId", out var tcidEl) ? GetStringOrSerialize(tcidEl) ?? "" : "",
                Name       = tfdEl.TryGetProperty("name",       out var nameEl) ? GetStringOrSerialize(nameEl) ?? "" : "",
                Params     = RedactPaths(tfdEl.TryGetProperty("params",  out var pEl)  ? GetStringOrSerialize(pEl)  : null, repoPath),
                RawArgs    = tfdEl.TryGetProperty("rawArgs", out var raEl)                                          ? GetStringOrSerialize(raEl) : null,
                Result     = RedactPaths(tfdEl.TryGetProperty("result",  out var rEl)  ? GetStringOrSerialize(rEl)  : null, repoPath),
                Status     = tfdEl.TryGetProperty("status",  out var sEl)              ? GetStringOrSerialize(sEl)  : null
            };
        }

        CursorThinking? thinking = null;
        if (r.TryGetProperty("thinking", out var thEl) && thEl.ValueKind == JsonValueKind.Object) {
            thinking = new CursorThinking {
                Text       = thEl.TryGetProperty("text",       out var tt) ? GetStringOrSerialize(tt) : null,
                Signature  = thEl.TryGetProperty("signature",  out var sg) ? GetStringOrSerialize(sg) : null,
                DurationMs = thEl.TryGetProperty("durationMs", out var dm) ? dm.GetInt32()  : 0
            };
        }

        CursorTokenCount? tokenCount = null;
        if (r.TryGetProperty("tokenCount", out var tcEl) && tcEl.ValueKind == JsonValueKind.Object) {
            tokenCount = new CursorTokenCount {
                InputTokens  = tcEl.TryGetProperty("inputTokens",  out var inp) ? inp.GetInt64() : 0,
                OutputTokens = tcEl.TryGetProperty("outputTokens", out var out_) ? out_.GetInt64() : 0
            };
        }

        return new CursorBubble {
            BubbleId       = bubbleId!,
            Type           = r.GetProperty("type").GetInt32(),
            CapabilityType = r.TryGetProperty("capabilityType", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number ? ctEl.GetInt32() : null,
            CreatedAtIso   = GetCreatedAtString(r),
            Text           = text,
            TokenCount     = tokenCount,
            ToolFormerData = tfd,
            Thinking       = thinking
        };
    }

    public static (string Key, string Value) MaybeTruncateBlob(string key, string content) {
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes <= BlobSoftCapBytes) return (key, content);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
        // Build marker JSON AOT-safe: no JsonObject string-value assignments (trips ThrowNotSupportedException under AOT).
        // Use JsonNode.Parse on a raw fragment instead.
        var marker = JsonNode.Parse($"{{\"truncated\":true,\"hash\":\"{hash}\",\"sizeBytes\":{bytes}}}")!.ToJsonString();
        return (key, marker);
    }

    /// <summary>
    /// Returns the element as a string if it is a JSON string, otherwise serializes it
    /// back to a JSON text (for cases where Cursor stores params/result as objects rather
    /// than JSON-encoded strings).
    /// </summary>
    static string? GetStringOrSerialize(JsonElement el) =>
        el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined
                ? null
                : el.GetRawText();

    static string GetCreatedAtString(JsonElement r) {
        if (!r.TryGetProperty("createdAt", out var el)) return "1970-01-01T00:00:00Z";
        if (el.ValueKind == JsonValueKind.String) return el.GetString()!;
        // Some bubbles store createdAt as a Unix ms timestamp (number)
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");
        return "1970-01-01T00:00:00Z";
    }

    static string? RedactPaths(string? json, string repoPath) {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(repoPath)) return json;

        // Trim trailing path separators so a repoPath ending in '/' or '\\'
        // matches the same as one without.
        var trimmed = repoPath.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return json;

        // Variants we might find in bubble JSON:
        //   forward          — POSIX-style forward slashes.
        //   backward         — Windows-native backslashes.
        //   backwardEscaped  — Windows backslashes in their JSON-escaped form,
        //                      which is how they appear inside the bubble JSON
        //                      when params/result was originally JSON-encoded
        //                      (one '\\' → two characters '\\\\').
        var forward         = trimmed.Replace('\\', '/');
        var backward        = trimmed.Replace('/', '\\');
        var backwardEscaped = backward.Replace("\\", "\\\\");

        // Windows filesystems are case-insensitive; POSIX is case-sensitive.
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Replace the escaped Windows form first — its substring 'backward' must
        // not be consumed mid-escape before we get a chance to see the full pair.
        var result = json;
        if (!string.Equals(forward, backward, StringComparison.Ordinal)) {
            result = result.Replace(backwardEscaped, "/repo", cmp);
            result = result.Replace(backward, "/repo", cmp);
        }
        result = result.Replace(forward, "/repo", cmp);
        return result;
    }
}
