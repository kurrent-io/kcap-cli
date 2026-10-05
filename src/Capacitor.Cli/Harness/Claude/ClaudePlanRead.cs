using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>A Claude <c>Read</c> of a Markdown file: the only read worth asking the server whether it
/// was a plan document the session has not declared.</summary>
sealed record ClaudePlanRead(string SessionId, string Path, string? Content) {
    /// <summary>The server corroborates a filename match from headings and checklist items, which sit
    /// near the top; the rest of a long document would only cost upload on the agent's tool path.</summary>
    internal const int MaxContentChars = 16_384;

    /// <summary>Null for anything but a PostToolUse of <c>Read</c> on a <c>.md</c> path with a session id.
    /// The session id comes back dashless, the form the disabled-session markers are keyed by.</summary>
    public static ClaudePlanRead? Parse(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch { return null; }
        if (hook is null) return null;
        if (Text(hook["hook_event_name"]) != "PostToolUse" || Text(hook["tool_name"]) != "Read") return null;

        var sessionId = Text(hook["session_id"])?.Replace("-", "");
        var path      = Text(hook["tool_input"]?["file_path"]);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(path)) return null;
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;

        return new(sessionId, path, Head(Text(hook["tool_response"]?["file"]?["content"])));
    }

    public JsonObject ToRequest() {
        var request = new JsonObject { ["session_id"] = SessionId, ["path"] = Path };
        if (Content is not null) request["content"] = Content;
        return request;
    }

    /// <summary>The nudge text from a <c>/hooks/plan-read</c> answer, or null when it carries none.</summary>
    public static string? ReadNudge(string responseBody) {
        try {
            return Text(JsonNode.Parse(responseBody)?["nudge"]) is { } nudge && !string.IsNullOrWhiteSpace(nudge) ? nudge : null;
        } catch {
            return null;
        }
    }

    public static string RenderHookOutput(string nudge) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "PostToolUse",
                ["additionalContext"] = nudge
            }
        }.ToJsonString();

    static string? Head(string? content) {
        if (content is null || content.Length <= MaxContentChars) return content;
        var end = char.IsHighSurrogate(content[MaxContentChars - 1]) ? MaxContentChars - 1 : MaxContentChars;
        return content[..end];
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
