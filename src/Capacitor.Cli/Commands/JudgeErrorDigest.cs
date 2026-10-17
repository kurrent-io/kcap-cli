using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>Renders the server's full error list as a page the judge can read inline: failures grouped by repeated operation,
/// then one short row per failure. The server's entries carry each call's whole input and output, which a long session
/// multiplies past any tool-result limit.</summary>
static class JudgeErrorDigest {
    public const int DefaultLimit = 40;
    public const int MaxLimit     = 100;

    const int OperationChars   = 200;
    const int ErrorHeadChars   = 160;
    const int ErrorTailChars   = 160;
    const int MaxGroups        = 30;
    const int MaxRefsPerGroup  = 20;

    sealed record Failure(string Ref, string? Timestamp, string Tool, string Operation, string Error, string? CallId);

    public static string Render(string body, int offset, int limit) {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return body;

        var entries      = doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        var multiSession = entries.Select(e => Str(e, "session_id")).Distinct(StringComparer.Ordinal).Count() > 1;
        var failures     = entries.Select(e => ToFailure(e, multiSession)).ToList();

        var repeated = failures
            .GroupBy(f => (f.Tool, f.Operation))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();

        var page = failures.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count;

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) {
            w.WriteStartObject();
            w.WriteNumber("total_failures", failures.Count);
            w.WriteNumber("distinct_operations", failures.Select(f => (f.Tool, f.Operation)).Distinct().Count());

            w.WriteStartArray("repeated_operations");
            foreach (var g in repeated.Take(MaxGroups)) {
                w.WriteStartObject();
                w.WriteString("tool", g.Key.Tool);
                w.WriteString("operation", g.Key.Operation);
                w.WriteNumber("failures", g.Count());
                w.WriteStartArray("refs");
                foreach (var f in g.Take(MaxRefsPerGroup)) w.WriteStringValue(f.Ref);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (repeated.Count > MaxGroups) w.WriteNumber("repeated_operations_omitted", repeated.Count - MaxGroups);

            w.WriteNumber("offset", offset);
            if (next < failures.Count) w.WriteNumber("next_offset", next);

            w.WriteStartArray("failures");
            foreach (var f in page) {
                w.WriteStartObject();
                w.WriteString("ref", f.Ref);
                if (f.Timestamp is not null) w.WriteString("timestamp", f.Timestamp);
                w.WriteString("tool", f.Tool);
                w.WriteString("operation", f.Operation);
                w.WriteString("error", f.Error);
                if (f.CallId is not null) w.WriteString("call_id", f.CallId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static Failure ToFailure(JsonElement e, bool multiSession) {
        var number  = e.TryGetProperty("event_number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : -1;
        var session = Str(e, "session_id");
        var agent   = Str(e, "agent_id");

        var reference = new StringBuilder();
        if (multiSession && session is not null) reference.Append(session.AsSpan(0, Math.Min(8, session.Length))).Append('/');
        if (agent is not null) reference.Append(agent).Append(':');
        reference.Append('#').Append(number);

        var input = e.TryGetProperty("tool_input", out var i) ? i : default;

        return new(reference.ToString(), Str(e, "timestamp"), Str(e, "tool_name") ?? "(unknown)", Operation(input),
            Excerpt(Str(e, "error") ?? ""), Str(e, "call_id"));
    }

    // The argument that names what the call acted on, so two attempts at the same thing share a key.
    static readonly string[] OperationKeys = ["command", "cmd", "file_path", "notebook_path", "path", "pattern", "url", "query"];

    static string Operation(JsonElement input) {
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var key in OperationKeys)
                if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    return Clip(Collapse(s), OperationChars);

        return input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : Clip(Collapse(input.GetRawText()), OperationChars);
    }

    // A failure's cause sits at either end of its output: the head names the command, the tail usually holds the error.
    static string Excerpt(string error) {
        var text = Collapse(error);
        return text.Length <= ErrorHeadChars + ErrorTailChars
            ? text
            : $"{text[..ErrorHeadChars]} … {text[^ErrorTailChars..]} ({text.Length} chars)";
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
