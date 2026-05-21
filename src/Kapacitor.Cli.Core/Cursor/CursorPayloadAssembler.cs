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

        string? text = r.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (text is { Length: > 0 } && Encoding.UTF8.GetByteCount(text) > BubbleTextCapBytes)
            text = text[..(BubbleTextCapBytes / 4)]; // crude cap; UTF-8 safe-enough

        CursorToolFormerData? tfd = null;
        if (r.TryGetProperty("toolFormerData", out var tfdEl) && tfdEl.ValueKind == JsonValueKind.Object) {
            tfd = new CursorToolFormerData {
                ToolCallId = tfdEl.GetProperty("toolCallId").GetString()!,
                Name       = tfdEl.GetProperty("name").GetString()!,
                Params     = RedactPaths(tfdEl.TryGetProperty("params",  out var pEl)  ? pEl.GetString()  : null, repoPath),
                RawArgs    = tfdEl.TryGetProperty("rawArgs", out var raEl)              ? raEl.GetString() : null,
                Result     = RedactPaths(tfdEl.TryGetProperty("result",  out var rEl)  ? rEl.GetString()  : null, repoPath),
                Status     = tfdEl.TryGetProperty("status",  out var sEl)              ? sEl.GetString()  : null
            };
        }

        CursorThinking? thinking = null;
        if (r.TryGetProperty("thinking", out var thEl) && thEl.ValueKind == JsonValueKind.Object) {
            thinking = new CursorThinking {
                Text       = thEl.TryGetProperty("text",       out var tt) ? tt.GetString() : null,
                Signature  = thEl.TryGetProperty("signature",  out var sg) ? sg.GetString() : null,
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
            BubbleId       = r.GetProperty("bubbleId").GetString()!,
            Type           = r.GetProperty("type").GetInt32(),
            CapabilityType = r.TryGetProperty("capabilityType", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number ? ctEl.GetInt32() : null,
            CreatedAtIso   = r.GetProperty("createdAt").GetString()!,
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

    static string? RedactPaths(string? json, string repoPath) {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(repoPath)) return json;
        // Cheap: textual replace. Bubbles are short; if false positives become a problem,
        // upgrade to JSON walk.
        return json.Replace(repoPath, "/repo");
    }
}
