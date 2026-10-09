using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>One gateway transcript entry, with what the sessionizer needs to decide whether it may be
/// sent yet. <see cref="Line"/> is the entry verbatim plus the owning <c>agentId</c>.</summary>
public sealed record GrokBotEntry(string Id, int Seq, long TimestampMs, bool IsStreaming, bool IsAwaitingAnswer, string Line) {
    public static GrokBotEntry? TryParse(JsonElement entry, string agentId) {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } entryId) return null;
        if (!entry.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt32(out var seqValue)) return null;
        if (!entry.TryGetProperty("timestampMs", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out var tsValue)) return null;

        var streaming = entry.TryGetProperty("isStreaming", out var s) && s.ValueKind == JsonValueKind.True;

        var awaiting = entry.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && (IsUnansweredWidget(entry, message) || IsUnansweredPermission(message));

        var node = JsonNode.Parse(entry.GetRawText())!.AsObject();
        node["agentId"] = agentId;

        return new GrokBotEntry(entryId, seqValue, tsValue, streaming, awaiting, node.ToJsonString());
    }

    static bool IsUnansweredWidget(JsonElement entry, JsonElement message) =>
        TypeOf(message) == "widget"
     && !entry.TryGetProperty("respondedValue", out _)
     && !(entry.TryGetProperty("widgetSkipped", out var skipped) && skipped.ValueKind == JsonValueKind.True);

    // An answered ask carries its outcome ("allowed", ...); anything else is still waiting on the user.
    static bool IsUnansweredPermission(JsonElement message) =>
        TypeOf(message) == "local-tool-permission"
     && !(message.TryGetProperty("ask", out var ask) && ask.ValueKind == JsonValueKind.Object
       && ask.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
       && status.GetString() is { Length: > 0 } outcome && outcome is not ("pending" or "requested"));

    static string? TypeOf(JsonElement message) =>
        message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
}
