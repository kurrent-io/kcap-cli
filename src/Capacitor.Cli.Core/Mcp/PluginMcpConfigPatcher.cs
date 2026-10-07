using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// Points the shipped Claude plugin's <c>.mcp.json</c> entries at the native binary. The plugin ships
/// <c>"command": "kcap"</c>, which on an npm install resolves to the node wrapper: one resident Node
/// runtime per MCP server per open Claude session. Only the canonical name/args pairs are rewritten,
/// and only when their command is the shipped literal or an absolute path to a kcap binary (a previous
/// patch gone stale); anything else is a customization and is kept.
/// </summary>
public static class PluginMcpConfigPatcher {
    public enum Outcome { Patched, Unchanged, Missing, Failed }

    static readonly JsonSerializerOptions WriteOpts = new() {
        WriteIndented = true,
        Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Patches <c>&lt;pluginDir&gt;/.mcp.json</c>. Never throws: a failure leaves the file as it
    /// was and comes back as <see cref="Outcome.Failed"/> with the reason.</summary>
    public static Outcome Patch(string pluginDir, string binaryPath, out string? error) {
        error = null;
        var path = Path.Combine(pluginDir, ".mcp.json");
        if (!File.Exists(path)) return Outcome.Missing;

        try {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (!PatchServers(root, binaryPath)) return Outcome.Unchanged;

            var tmp = $"{path}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
            try {
                File.WriteAllText(tmp, root!.ToJsonString(WriteOpts) + "\n");
                File.Move(tmp, path, overwrite: true);
            } catch {
                try { File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }

            return Outcome.Patched;
        } catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) {
            error = ex.Message;
            return Outcome.Failed;
        }
    }

    /// <summary>Rewrites the canonical entries of <paramref name="root"/> in place; true when any changed.
    /// Throws <see cref="InvalidDataException"/> on a shape that is not the plugin config.</summary>
    public static bool PatchServers(JsonNode? root, string binaryPath) {
        if (root is not JsonObject obj) throw new InvalidDataException("config is not an object");
        if (obj["mcpServers"] is not JsonObject servers) throw new InvalidDataException("mcpServers is not an object");

        var changed = false;
        foreach (var server in KcapMcpServers.All) {
            if (servers[server.Name] is not JsonObject entry) continue;
            if (entry["args"] is not JsonArray args || args.Count != server.Args.Length
             || !args.Select((a, i) => a is JsonValue v && v.TryGetValue<string>(out var s) && s == server.Args[i]).All(static m => m))
                continue;
            if (entry["command"] is not JsonValue c || !c.TryGetValue<string>(out var command) || !IsPatchable(command)) continue;
            if (command == binaryPath) continue;

            entry["command"] = binaryPath;
            changed = true;
        }

        return changed;
    }

    /// <summary>The shipped literal, or an absolute path (either OS's flavour) whose file is a kcap binary.</summary>
    public static bool IsPatchable(string command) {
        if (command == KcapMcpServers.Command) return true;

        var windowsRooted = command.Length >= 3 && char.IsAsciiLetter(command[0]) && command[1] == ':' && command[2] is '\\' or '/';
        if (!command.StartsWith('/') && !command.StartsWith('\\') && !windowsRooted) return false;

        var name = command[(command.LastIndexOfAny(['/', '\\']) + 1)..];

        return name.Equals("kcap", StringComparison.OrdinalIgnoreCase) || name.Equals("kcap.exe", StringComparison.OrdinalIgnoreCase);
    }
}
