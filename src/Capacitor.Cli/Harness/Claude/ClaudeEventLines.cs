using System.Text.Json;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Which Claude transcript lines the server's normalizer can turn into a stored event. Everything else —
/// <c>system</c> hook and timing records, <c>last-prompt</c>, <c>file-history-snapshot</c>, title lines,
/// attachments other than a queued command, and any type Claude Code adds later — leaves no line number
/// in the server's watermark, so it must not decide that a session is unfinished.
/// </summary>
/// <remarks>Mirrors the server's Claude normalizer. A line it cannot rule out counts as one that can add
/// events: a wrong yes costs a re-sent tail, a wrong no leaves a line unsent.</remarks>
internal static class ClaudeEventLines {
    public static bool CanAddEvents(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            return root.Str("type") switch {
                "user"       => root.Obj("message")?.Prop("content") is { } content && HasContent(content),
                "assistant"  => root.Obj("message")?.Arr("content") is { } blocks && StartsWithRenderedBlock(blocks),
                "attachment" => root.Obj("attachment")?.Str("type") == "queued_command",
                "system"     => root.Str("subtype") == "local_command",
                _            => false,
            };
        } catch (JsonException) {
            return false;
        }
    }

    static bool HasContent(JsonElement content) =>
        content.ValueKind switch {
            JsonValueKind.String => true,
            JsonValueKind.Array  => content.GetArrayLength() > 0,
            _                    => false,
        };

    static bool StartsWithRenderedBlock(JsonElement blocks) =>
        blocks.GetArrayLength() > 0 && blocks[0].Str("type") is "text" or "tool_use" or "thinking";
}
