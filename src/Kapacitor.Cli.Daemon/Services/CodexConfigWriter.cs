using Kapacitor.Cli.Core;
using Microsoft.Extensions.Logging;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Kapacitor.Cli.Daemon.Services;

/// <summary>
/// Writes the per-worktree pre-trust entry into <c>~/.codex/config.toml</c>:
/// <code>
/// [projects."/abs/worktree/path"]
/// trust_level = "trusted"
/// </code>
/// Tomlyn's TomlTable mode round-trips preserve all other top-level tables
/// (model, mcp_servers, plugins, marketplaces, ad-hoc user keys) but DO NOT
/// preserve user comments or original formatting. This is acceptable because
/// the file is daemon-managed and human edits are expected to be sparse.
/// </summary>
internal static class CodexConfigWriter {
    static readonly Lock              _writeLock    = new();
    static readonly TomlTableTypeInfo _tomlTypeInfo = new();

    public static void TrustWorktree(string worktreePath, ILogger logger) {
        lock (_writeLock) {
            var configPath = Path.Combine(CodexPaths.Home, "config.toml");

            TomlTable root;

            if (File.Exists(configPath)) {
                try {
                    root = TomlSerializer.Deserialize(File.ReadAllText(configPath), _tomlTypeInfo.TableInfo) ?? [];
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to parse {Path}; aborting pre-trust", configPath);

                    return;
                }
            } else {
                root = new TomlTable();
            }

            if (!root.TryGetValue("projects", out var projectsObj) || projectsObj is not TomlTable projects) {
                projects         = new TomlTable();
                root["projects"] = projects;
            }

            if (!projects.TryGetValue(worktreePath, out var entryObj) || entryObj is not TomlTable entry) {
                entry                  = new TomlTable();
                projects[worktreePath] = entry;
            }

            var alreadyTrusted = entry.TryGetValue("trust_level", out var existing) &&
                existing is string s                                                &&
                string.Equals(s, "trusted", StringComparison.Ordinal);

            if (alreadyTrusted) return;

            entry["trust_level"] = "trusted";

            try {
                // First-time users have no ~/.codex; create it before the atomic rename.
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                WriteTomlAtomic(configPath, root);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to write {Path}; pre-trust not persisted", configPath);
            }
        }
    }

    static void WriteTomlAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, TomlSerializer.Serialize(root, _tomlTypeInfo.TableInfo));

        try {
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch {
                /* best-effort */
            }

            throw;
        }
    }

    /// <summary>
    /// AOT-safe accessor for <see cref="TomlTypeInfo{T}"/> of <see cref="TomlTable"/>.
    /// <see cref="TomlSerializerContext.GetBuiltInTypeInfo{T}"/> is protected, so a thin
    /// subclass is used to surface the built-in untyped-model metadata without reflection.
    /// </summary>
    sealed class TomlTableTypeInfo : TomlSerializerContext {
        static readonly TomlSerializerOptions DefaultOptions = new();

        public readonly TomlTypeInfo<TomlTable> TableInfo = GetBuiltInTypeInfo<TomlTable>(DefaultOptions)!;

        public override TomlTypeInfo? GetTypeInfo(Type type, TomlSerializerOptions options) =>
            type == typeof(TomlTable) ? TableInfo : null;
    }
}
