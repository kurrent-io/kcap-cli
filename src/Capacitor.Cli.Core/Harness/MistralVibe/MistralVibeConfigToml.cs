using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Toml;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>
/// Registers the kcap MCP servers into Mistral Vibe's <c>~/.vibe/config.toml</c>, under its
/// <c>[[mcp_servers]]</c> array-of-tables (<c>name</c>/<c>transport</c>/<c>command</c>/<c>args</c>).
/// Vibe's MCP config is TOML, so it is deliberately absent from <see cref="HarnessMcpProjections"/>
/// (the JSON writer) — exactly as Codex is.
///
/// <para>Ownership is proven by shape rather than a side ledger: an entry is kcap's only when its
/// <c>name</c> is one of <see cref="KcapMcpServers.All"/> AND its <c>command</c> resolves to the
/// <c>kcap</c> binary. Register heals those and leaves everything else; unregister removes only
/// those. A user server merely sharing a kcap name but a different command is never touched.</para>
/// </summary>
public static class MistralVibeConfigToml {
    const string ServersKey = "mcp_servers";

    static readonly HashSet<string> KcapServerNames =
        new(KcapMcpServers.All.Select(s => s.Name), StringComparer.Ordinal);

    public static TomlConfigFile.Outcome RegisterKcapMcpServers(string configPath,
                                                                Func<string?>? resolveBinaryPath = null) {
        var command = KcapBinaryCommand.Resolve(resolveBinaryPath);
        return TomlConfigFile.Edit(configPath, root => RegisterMutate(root, command));
    }

    public static TomlConfigFile.Outcome UnregisterKcapMcpServers(string configPath) =>
        TomlConfigFile.Edit(configPath, RemoveMutate);

    /// <summary>The model Vibe is configured to run: <c>active_model</c> resolved through the
    /// <c>[[models]]</c> table that defines its alias. Null when unset or not defined there, since
    /// Vibe then falls back to a default kcap cannot see, and a guessed model would misprice.</summary>
    public static string? ConfiguredModel(string configPath) {
        if (TomlConfigFile.Read(configPath) is not { } root) return null;
        if (!root.TryGetValue("active_model", out var a) || a is not string { Length: > 0 } active) return null;
        if (!root.TryGetValue("models", out var m) || m is not TomlTableArray models) return null;

        foreach (var model in models)
            if (model.TryGetValue("alias", out var alias) && alias as string == active
             || model.TryGetValue("name", out var named) && named as string == active)
                return model.TryGetValue("name", out var name) ? name as string : null;

        return null;
    }

    /// <summary>Whether kcap currently owns any <c>[[mcp_servers]]</c> entry — the "is MCP already
    /// installed?" probe.</summary>
    public static bool OwnsAnything(string configPath) =>
        TomlConfigFile.Read(configPath) is { } root
            && root.TryGetValue(ServersKey, out var v) && v is TomlTableArray array
            && array.Any(IsKcapOwned);

    static bool RegisterMutate(TomlTable root, string command) {
        if (root.TryGetValue(ServersKey, out var existing) && existing is not TomlTableArray)
            return false; // unknown shape — never clobber

        var array   = existing as TomlTableArray;
        var changed = false;

        foreach (var descriptor in KcapMcpServers.ForHarness("mistral-vibe")) {
            var desired = BuildEntry(descriptor, command);
            var index   = IndexOfNamed(array, descriptor.Name);

            if (index >= 0) {
                if (!IsKcapOwned(array![index])) continue;      // a user's same-named server — leave it
                if (!EntryMatches(array[index], desired)) {
                    array[index] = desired;                      // heal a stale command/args/timeout
                    changed = true;
                }
            } else {
                array ??= [];
                array.Add(desired);
                changed = true;
            }
        }

        if (changed && array is not null) root[ServersKey] = array;
        return changed;
    }

    static bool RemoveMutate(TomlTable root) {
        if (!root.TryGetValue(ServersKey, out var v) || v is not TomlTableArray array) return false;

        var removed = false;
        for (var i = array.Count - 1; i >= 0; i--) {
            if (!IsKcapOwned(array[i])) continue;
            array.RemoveAt(i);
            removed = true;
        }

        if (!removed) return false;
        if (array.Count == 0) root.Remove(ServersKey);
        return true;
    }

    static TomlTable BuildEntry(KcapMcpServer descriptor, string command) {
        var table = new TomlTable {
            ["name"]      = descriptor.Name,
            ["transport"] = "stdio",
            ["command"]   = command,
            ["args"]      = TomlConfigFile.StringArray(descriptor.Args),
        };
        if (descriptor.ToolTimeout is { } timeout) table["tool_timeout_sec"] = (long)timeout.TotalSeconds;
        return table;
    }

    static int IndexOfNamed(TomlTableArray? array, string name) {
        if (array is null) return -1;
        for (var i = 0; i < array.Count; i++)
            if (array[i].TryGetValue("name", out var n) && n is string s && s == name) return i;
        return -1;
    }

    static bool IsKcapOwned(TomlTable entry) =>
        entry.TryGetValue("name", out var n) && n is string name && KcapServerNames.Contains(name)
     && entry.TryGetValue("command", out var c) && c is string cmd && CommandIsKcap(cmd);

    static bool CommandIsKcap(string command) {
        var slash = command.LastIndexOfAny(['/', '\\']);
        var exe   = slash >= 0 ? command[(slash + 1)..] : command;
        return exe is "kcap" or "kcap.exe";
    }

    /// <summary>Field equality over what kcap writes (command, args, timeout) — enough to decide
    /// whether an owned entry needs healing without a full structural compare.</summary>
    static bool EntryMatches(TomlTable existing, TomlTable desired) {
        if (StringField(existing, "command") != StringField(desired, "command")) return false;
        if (LongField(existing, "tool_timeout_sec") != LongField(desired, "tool_timeout_sec")) return false;

        var a = existing.TryGetValue("args", out var av) && av is TomlArray aa ? aa : null;
        var b = desired.TryGetValue("args", out var bv) && bv is TomlArray bb ? bb : null;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] as string != b[i] as string) return false;
        return true;
    }

    static string? StringField(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is string s ? s : null;

    static long? LongField(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is long l ? l : null;
}
