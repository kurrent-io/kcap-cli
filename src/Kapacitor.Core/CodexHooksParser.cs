using System.Text.Json.Nodes;

namespace kapacitor;

/// <summary>
/// Parsing helpers for <c>~/.codex/hooks.json</c> (and <c>&lt;repo&gt;/.codex/hooks.json</c>
/// in project-scope installs). Shared by the CLI's <c>plugin install --codex</c>
/// command (which writes the file) and the daemon's <c>CodexLauncher</c> preflight
/// (which reads it before spawning a hosted Codex agent).
/// </summary>
public static class CodexHooksParser {
    /// <summary>Hook event names Codex CLI emits.</summary>
    public static readonly string[] CodexHookEvents = [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop"
    ];

    /// <summary>
    /// Returns true if <paramref name="entry"/> is a hooks.json group whose
    /// <c>hooks[].command</c> contains <c>kapacitor codex-hook</c>.
    /// </summary>
    public static bool EntryReferencesKapacitorCodexHook(JsonNode? entry) {
        if (entry?["hooks"] is not JsonArray hooks) return false;

        foreach (var hook in hooks) {
            if (hook?["command"] is JsonValue jv &&
                jv.TryGetValue<string>(out var cmd) &&
                cmd.Contains("kapacitor codex-hook")) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if every event in <paramref name="events"/> has at least one
    /// hooks.json entry that invokes <c>kapacitor codex-hook</c>.
    /// </summary>
    public static bool HasKapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            var any = false;

            foreach (var entry in entries) {
                if (EntryReferencesKapacitorCodexHook(entry)) {
                    any = true;
                    break;
                }
            }

            if (!any) return false;
        }

        return true;
    }
}
