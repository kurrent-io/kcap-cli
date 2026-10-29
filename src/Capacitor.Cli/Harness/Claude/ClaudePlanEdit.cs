using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>A Claude file write: the moment a document the session declared may have changed under
/// the snapshot the server holds.</summary>
sealed record ClaudePlanEdit(string SessionId, string Path) {
    static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit" };

    /// <summary>Null for anything but a PostToolUse of a file-writing tool with a session id and an
    /// absolute path. The session id comes back canonical, the form the ledger is keyed by.</summary>
    public static ClaudePlanEdit? Parse(string body) {
        JsonObject? hook;
        try { hook = JsonNode.Parse(body) as JsonObject; } catch { return null; }
        if (hook is null) return null;
        if (Text(hook["hook_event_name"]) != "PostToolUse" || Text(hook["tool_name"]) is not { } tool || !WriteTools.Contains(tool)) return null;

        var sessionId = WorkContextIds.CanonicalSessionId(Text(hook["session_id"]));
        var path      = Text(hook["tool_input"]?["file_path"]);
        if (sessionId is null || string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;

        return new(sessionId, path);
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
