using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>Renders the server's full error list as a page the judge can read inline: failures grouped by repeated operation,
/// then one short row per failure. The server's entries carry each call's whole input and output, which a long session
/// multiplies past any tool-result limit. Every page is one JSON document within <see cref="JudgeResultCap.MaxChars"/>,
/// and its <c>next_offset</c> follows the last row it holds.</summary>
static class JudgeErrorDigest {
    public const int DefaultLimit = 40;
    public const int MaxLimit     = 100;

    const int OperationChars  = 200;
    const int ErrorHeadChars  = 160;
    const int ErrorTailChars  = 160;
    const int MaxRefsPerGroup = 20;

    // Room for the page's own fields around its groups and rows; repeated groups take at most half of what is left.
    const int EnvelopeChars = 400;
    const int GroupBudget   = (JudgeResultCap.MaxChars - EnvelopeChars) / 2;

    sealed record Failure(string Ref, string? Timestamp, string Tool, string Operation, string? Key, string Error, string? AgentId, string? CallId);

    public static string Render(string body, string boundSessionId, int offset, int limit) {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.IsArray) return body;

        var entries      = doc.RootElement.EnumerateArray().Where(e => e.IsObject).ToList();
        var multiSession = entries.Select(e => e.Str("session_id")).Distinct(StringComparer.Ordinal).Count() > 1;
        var failures     = entries.Select(e => ToFailure(e, multiSession, boundSessionId)).ToList();

        // A failure whose input names nothing it acted on has no operation to repeat, so it never joins a group.
        var repeated = failures
            .Where(f => f.Key is not null)
            .GroupBy(f => (f.Tool, f.Key))
            .Where(g => g.Skip(1).Any())
            .OrderByDescending(g => g.Count())
            .ToList();

        var groups = new List<string>();
        var used   = 0;
        foreach (var g in repeated) {
            var fragment = Fragment(w => WriteGroup(w, g.First().Operation, g.Key.Tool, g.ToList()));
            if (used + fragment.Length + 1 > GroupBudget) break;
            groups.Add(fragment);
            used += fragment.Length + 1;
        }

        var rows = new List<string>();
        var room = JudgeResultCap.MaxChars - EnvelopeChars - used;
        foreach (var f in failures.Skip(offset).Take(limit)) {
            var fragment = Fragment(w => WriteFailure(w, f));
            if (rows.Count > 0 && fragment.Length + 1 > room) break;
            rows.Add(fragment);
            room -= fragment.Length + 1;
        }

        var next = offset + rows.Count;

        var head = Fragment(w => {
            w.WriteStartObject();
            w.WriteNumber("total_failures", failures.Count);
            w.WriteNumber("distinct_operations", failures.Select(f => (f.Tool, f.Key ?? f.Ref)).Distinct().Count());
            if (repeated.Count > groups.Count) w.WriteNumber("repeated_operations_omitted", repeated.Count - groups.Count);
            w.WriteNumber("offset", offset);
            if (next < failures.Count) w.WriteNumber("next_offset", next);
            w.WriteEndObject();
        });

        return $"{head[..^1]},\"repeated_operations\":[{string.Join(',', groups)}],\"failures\":[{string.Join(',', rows)}]}}";
    }

    static void WriteGroup(Utf8JsonWriter w, string operation, string tool, List<Failure> members) {
        w.WriteStartObject();
        w.WriteString("tool", tool);
        w.WriteString("operation", operation);
        w.WriteNumber("failures", members.Count);
        w.WriteStartArray("refs");
        foreach (var f in members.Take(MaxRefsPerGroup)) w.WriteStringValue(f.Ref);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteFailure(Utf8JsonWriter w, Failure f) {
        w.WriteStartObject();
        w.WriteString("ref", f.Ref);
        if (f.Timestamp is not null) w.WriteString("timestamp", f.Timestamp);
        w.WriteString("tool", f.Tool);
        w.WriteString("operation", f.Operation);
        w.WriteString("error", f.Error);
        if (f.CallId is not null) w.WriteString("call_id", f.CallId);
        if (f.CallId is not null && f.AgentId is not null) w.WriteString("agent_id", f.AgentId);
        w.WriteEndObject();
    }

    static string Fragment(Action<Utf8JsonWriter> write) {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, JudgeResultCap.Writer)) write(w);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static Failure ToFailure(JsonElement e, bool multiSession, string boundSessionId) {
        var session = e.Str("session_id");
        var agent   = e.Str("agent_id");

        var reference = new StringBuilder();
        if (multiSession && session is not null) reference.Append(session.AsSpan(0, Math.Min(8, session.Length))).Append('/');
        if (agent is not null) reference.Append(agent).Append(':');
        reference.Append('#').Append(e.Num("event_number") ?? -1);

        var key = Operation(e.Prop("tool_input"));

        // get_tool_result reads only the bound session, so a continuation's call id would not resolve.
        var callId = SameSession(session, boundSessionId) ? e.Str("call_id") : null;

        return new(reference.ToString(), e.Str("timestamp"), e.Str("tool_name") ?? "(unknown)",
            key is null ? "" : Clip(key, OperationChars), key, Excerpt(e.Str("error") ?? ""), agent, callId);
    }

    static bool SameSession(string? a, string b) =>
        a is not null && string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    // The argument that names what the call acted on, so two attempts at the same thing share a key.
    static readonly string[] OperationKeys = ["command", "cmd", "file_path", "notebook_path", "path", "pattern", "url", "query"];

    static string? Operation(JsonElement? input) {
        if (input is not { } value || value.IsNull) return null;

        foreach (var key in OperationKeys)
            if (value.Str(key) is { Length: > 0 } s)
                return Collapse(s);

        var raw = Collapse(value.GetRawText());
        return raw is "{}" or "[]" or "" ? null : raw;
    }

    // A failure's cause sits at either end of its output: the head names the command, the tail usually holds the error.
    static string Excerpt(string error) {
        var text = Collapse(error);
        if (text.Length <= ErrorHeadChars + ErrorTailChars) return text;

        var head = JudgeText.Boundary(text, ErrorHeadChars);
        var tail = JudgeText.Boundary(text, text.Length - ErrorTailChars);
        return $"{text[..head]} … {text[tail..]} ({text.Length} chars)";
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..JudgeText.Boundary(s, max)] + "…";

    static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
