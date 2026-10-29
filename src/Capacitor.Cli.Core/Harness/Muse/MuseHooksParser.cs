using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>Muse's <c>hooks</c> block: event name → array of groups <c>{matcher?, hooks:[{type,command,timeout}]}</c>.
/// <c>UserPromptSubmit</c> and <c>Stop</c> reject a matcher, so kcap writes none anywhere.</summary>
public static class MuseHooksParser {
    public const string HookCommand = "kcap hook --muse --no-update-check";

    public static readonly string[] MuseHookEvents = [
        "SessionStart", "UserPromptSubmit", "SubagentStart", "SubagentStop", "Stop", "SessionEnd",
    ];

    /// <summary>Muse waits for SessionEnd hooks (shutdown_steps.hooks_ms), so that one gets the final drain budget.</summary>
    public static int TimeoutSecondsFor(string eventName) => eventName == "SessionEnd" ? 15 : 5;

    public static JsonObject BuildKcapGroup(string eventName) =>
        new() {
            ["hooks"] = new JsonArray(new JsonObject {
                ["type"]    = "command",
                ["command"] = HookCommand,
                ["timeout"] = TimeoutSecondsFor(eventName),
            }),
        };

    public static bool EntryReferencesCapacitorMuseHook(JsonNode? group) {
        if (group is not JsonObject g) return false;
        if (g["hooks"] is JsonArray hooks) return hooks.Any(IsCapacitorMuseHook);
        return IsCapacitorMuseHookCommand(CommandOf(g["command"]));
    }

    /// <summary>Whether one entry of a group's <c>hooks</c> array is kcap's command.</summary>
    public static bool IsCapacitorMuseHook(JsonNode? hook) =>
        hook is JsonObject h && IsCapacitorMuseHookCommand(CommandOf(h["command"]));

    // A user's own hook may carry a non-string command; that is simply not ours.
    static string? CommandOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var command) ? command : null;

    public static bool IsCapacitorMuseHookCommand(string? command) {
        if (command is null) return false;
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 3 or > 4) return false;
        var exe = tokens[0][(tokens[0].LastIndexOfAny(['/', '\\']) + 1)..];
        return exe is "kcap" or "kcap.exe"
            && tokens[1] == "hook" && tokens[2] == "--muse"
            && (tokens.Length == 3 || tokens[3] == "--no-update-check");
    }

    public static IReadOnlySet<string> InstalledEvents(JsonObject root) {
        if (root["hooks"] is not JsonObject hooks) return FrozenSet<string>.Empty;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (eventName, groups) in hooks)
            if (groups is JsonArray arr && arr.Any(EntryReferencesCapacitorMuseHook)) found.Add(eventName);
        return found.Count == 0 ? FrozenSet<string>.Empty : found;
    }

    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        var installed = InstalledEvents(root);
        return events.All(installed.Contains);
    }
}
