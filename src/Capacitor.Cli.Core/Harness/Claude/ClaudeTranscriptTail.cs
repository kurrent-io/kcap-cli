using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>The last assistant text in a Claude Code transcript, read from the file's tail so a long
/// session costs no more than a short one.</summary>
public static class ClaudeTranscriptTail {
    public const int TailBytes = 256 * 1024;

    /// <summary>The most recent main-agent assistant text, keeping its last <paramref name="maxChars"/>
    /// characters — the end of a closing message is what says whether it asks a question. Null when
    /// the file is missing, unreadable, or holds no assistant text within the tail.</summary>
    public static string? LastAssistantText(string? path, int maxChars) {
        if (string.IsNullOrEmpty(path) || maxChars <= 0) return null;

        string tail;
        bool   truncated;
        try {
            // Shared read/write/delete: the agent is still writing its own transcript.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start  = Math.Max(0, length - TailBytes);
            stream.Position = start;
            var buffer = new byte[length - start];
            var read   = 0;
            while (read < buffer.Length) {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            tail      = Encoding.UTF8.GetString(buffer, 0, read);
            truncated = start > 0;
        } catch {
            return null;
        }

        var lines = tail.Split('\n');
        // A tail that starts mid-file starts mid-line; that first fragment is not JSON.
        var first = truncated ? 1 : 0;

        for (var i = lines.Length - 1; i >= first; i--) {
            if (TextOf(lines[i]) is not { } text) continue;
            return text.Length <= maxChars ? text : text[^maxChars..];
        }

        return null;
    }

    /// <summary>The whole text of a main-agent assistant record — every text block in order, or the
    /// content itself when it is a string. A subagent's record (<c>isSidechain</c>) is not the agent
    /// the user is talking to.</summary>
    static string? TextOf(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (root.Str("type") != "assistant") return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            if (root.Obj("message") is not { } message || !message.TryGetProperty("content", out var content)) return null;

            if (content.ValueKind == JsonValueKind.String) return content.GetString()?.Trim() is { Length: > 0 } whole ? whole : null;
            if (content.ValueKind != JsonValueKind.Array) return null;

            var texts = new List<string>();
            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) texts.Add(text);
            }
            return texts.Count == 0 ? null : string.Join("\n\n", texts);
        } catch {
            return null;
        }
    }
}
