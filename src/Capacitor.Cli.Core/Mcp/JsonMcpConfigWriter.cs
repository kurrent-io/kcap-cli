using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// Read-modify-write engine for a harness's JSON MCP config file. Mirrors
/// <c>CodexConfigToml</c>: fail-closed on a malformed/wrong-type file (never clobber),
/// non-destructive (preserves user servers + surrounding config), idempotent, atomic.
/// Uses the JsonNode DOM — reflection-free and AOT-safe.
/// </summary>
public static class JsonMcpConfigWriter {
    static readonly Lock _writeLock = new();
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public enum Change { Unchanged, Updated, Failed }

    public static Change Register(string configPath, IReadOnlyList<KcapMcpServer> servers,
                                  McpConfigShape shape, string? cwd, IMcpMarker marker) =>
        Update(configPath, root => {
            var block   = GetOrAddObject(root, shape.BlockKey); // throws on wrong-type → Failed
            var changed = false;
            var written = new List<string>();

            foreach (var s in servers) {
                if (block[s.Name] is JsonNode existing) {
                    // Already present: a prior kcap registration is already correct, and a
                    // user look-alike must not be clobbered either way. Only a missing entry
                    // is written — mirrors CodexConfigToml's "only missing servers are added"
                    // idempotency contract.
                    if (marker.Owns(configPath, s.Name, existing)) written.Add(s.Name);
                    continue;
                }
                block[s.Name] = RenderEntry(s, shape, cwd);
                written.Add(s.Name);
                changed = true;
            }

            if (changed) marker.Record(configPath, written);
            return changed;
        });

    public static Change Unregister(string configPath, McpConfigShape shape, IMcpMarker marker) =>
        Update(configPath, root => {
            if (root[shape.BlockKey] is not JsonObject block) return false;
            var changed = false;

            foreach (var name in marker.Owned(configPath).ToArray())
                if (block[name] is JsonNode entry && marker.Owns(configPath, name, entry) && block.Remove(name))
                    changed = true;

            if (block.Count == 0 && root.Remove(shape.BlockKey)) changed = true;
            if (changed) marker.Clear(configPath);
            return changed;
        });

    static JsonObject RenderEntry(KcapMcpServer s, McpConfigShape shape, string? cwd) {
        var o = new JsonObject();
        if (shape.TypeValue is not null) o["type"] = shape.TypeValue;

        if (shape.CommandAsArgvArray) {
            // Use the implicit string -> JsonValue conversion (cast to JsonNode?) rather
            // than JsonValue.Create / collection expressions, which lower to generic
            // Add<T> and trip NativeAOT (IL3050). Matches ReviewLaunchBuilder's pattern.
            var argv = new JsonArray();
            argv.Add((JsonNode?)KcapMcpServers.Command);
            foreach (var a in s.Args) argv.Add((JsonNode?)a);
            o["command"] = argv;
        } else {
            o["command"] = KcapMcpServers.Command;
            var args = new JsonArray();
            foreach (var a in s.Args) args.Add((JsonNode?)a);
            o["args"] = args;
        }

        if (cwd is not null && s.NeedsProjectCwd) o["cwd"] = cwd;
        if (shape.Enable == EnableStyle.EnabledTrue) o["enabled"] = true;
        return o;
    }

    static Change Update(string configPath, Func<JsonObject, bool> mutate) {
        lock (_writeLock) {
            JsonObject root;

            if (File.Exists(configPath)) {
                try {
                    var parsed = JsonNode.Parse(File.ReadAllText(configPath));
                    if (parsed is not JsonObject obj) return Change.Failed; // wrong top-level type
                    root = obj;
                } catch {
                    return Change.Failed; // malformed — never clobber
                }
            } else {
                root = new JsonObject();
            }

            bool changed;
            try { changed = mutate(root); }
            catch { return Change.Failed; } // e.g. wrong-type block

            if (!changed) return Change.Unchanged;

            try {
                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(configPath, root);
                return Change.Updated;
            } catch { return Change.Failed; }
        }
    }

    static JsonObject GetOrAddObject(JsonObject parent, string key) {
        if (parent.TryGetPropertyValue(key, out var v)) {
            if (v is JsonObject obj) return obj;
            throw new InvalidOperationException($"`{key}` is present but not an object; refusing to overwrite.");
        }
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    static void WriteAtomic(string path, JsonNode root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { /* best-effort */ } throw; }
    }
}
