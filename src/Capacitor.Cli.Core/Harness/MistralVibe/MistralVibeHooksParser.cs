using Tomlyn.Model;

namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>
/// Parsing + entry-building helpers for the <c>[[hooks]]</c> array in Mistral Vibe's
/// <c>~/.vibe/hooks.toml</c>. Unlike Gemini (an object keyed by event name), Vibe stores every hook
/// in one array-of-tables, each element carrying its own <c>type</c> field. kcap registers ONE
/// command hook per lifecycle type; the command (<see cref="HookCommand"/>) self-routes on the
/// payload's <c>hook_event_name</c>, so every type shares the same command.
/// </summary>
public static class MistralVibeHooksParser {
    /// <summary>The top-level TOML key holding the array-of-tables.</summary>
    public const string HooksKey = "hooks";

    /// <summary>The single dispatcher command kcap installs for every type.</summary>
    public const string HookCommand = "kcap hook --mistral-vibe";

    /// <summary>
    /// Hook types Vibe fires. <c>pre_tool</c>/<c>post_tool</c> carry tool content kcap takes from the
    /// transcript, not the hook; <c>post_agent</c> marks a settled assistant turn. Subscribing to all
    /// three keeps the watcher alive across a session and drains it when the agent stops.
    /// </summary>
    public static readonly string[] VibeHookTypes = [
        "pre_tool",
        "post_tool",
        "post_agent"
    ];

    /// <summary>Vibe drops a hook whose name repeats one already loaded from any hook file, so each
    /// type's entry needs its own name.</summary>
    public static string HookName(string type) => "kcap-" + type;

    /// <summary>The <c>[[hooks]]</c> entry kcap installs for one type: <c>{ name, type, command,
    /// timeout }</c>. Vibe's <c>timeout</c> is seconds (float); 30 matches the budget the other
    /// harnesses' entries use.</summary>
    public static TomlTable BuildKcapEntry(string type) =>
        new() {
            ["name"]    = HookName(type),
            ["type"]    = type,
            ["command"] = HookCommand,
            ["timeout"] = 30.0,
        };

    /// <summary>True when <paramref name="entry"/> is a <c>[[hooks]]</c> table whose <c>command</c>
    /// invokes the kcap Vibe dispatcher. Matched as executable basename + exact args rather than a
    /// substring, so a foreign command cannot spoof ownership by embedding the marker in a comment —
    /// which matters because uninstall removes exactly what this recognises.</summary>
    public static bool EntryReferencesCapacitorVibeHook(TomlTable? entry) =>
        entry is not null
     && entry.TryGetValue("command", out var c)
     && c is string cmd
     && IsCapacitorVibeHookCommand(cmd);

    /// <summary>True when <paramref name="command"/> invokes <c>kcap hook --mistral-vibe</c> — the real
    /// executable + arguments, basename-compared so a path-qualified <c>/usr/local/bin/kcap</c> still
    /// matches while a marker buried in a later argument does not.</summary>
    public static bool IsCapacitorVibeHookCommand(string? command) {
        if (command is null) return false;

        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var exe   = ExecutableName(tokens[0]);
        return exe is "kcap" or "kcap.exe" && tokens is [_, "hook", "--mistral-vibe"];
    }

    static string ExecutableName(string token) {
        var slash = token.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? token[(slash + 1)..] : token;
    }

    /// <summary>The <c>[[hooks]]</c> array, or an empty one when the key is missing or the wrong
    /// shape (a scalar <c>hooks</c> is not ours to read).</summary>
    public static TomlTableArray HooksArray(TomlTable root) =>
        root.TryGetValue(HooksKey, out var v) && v is TomlTableArray arr ? arr : [];

    /// <summary>True when every type in <paramref name="types"/> has at least one <c>[[hooks]]</c>
    /// entry of that type referencing the kcap Vibe command.</summary>
    public static bool HasCapacitorHooksFor(TomlTable root, IEnumerable<string> types) {
        var entries = HooksArray(root);
        foreach (var type in types) {
            var found = entries.Any(e =>
                e.TryGetValue("type", out var t) && t is string ts && ts == type &&
                EntryReferencesCapacitorVibeHook(e));
            if (!found) return false;
        }
        return true;
    }
}
