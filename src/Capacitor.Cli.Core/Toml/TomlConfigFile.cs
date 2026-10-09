using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Capacitor.Cli.Core.Toml;

/// <summary>
/// Shared load → mutate → atomic-write engine for a tool-managed TOML file (Vibe's
/// <c>hooks.toml</c> and <c>config.toml</c>). Owns the AOT-safe Tomlyn plumbing — the untyped
/// <see cref="TomlTable"/> model needs type metadata under NativeAOT, surfaced here without
/// reflection — plus a cross-process lock, symlink rejection, and an owner-only atomic write.
///
/// <para>TomlTable round-trips preserve every other top-level table and key but NOT comments or
/// original formatting — acceptable for a file kcap and the vendor both write and humans edit
/// sparsely. This lives outside <c>Harness/</c> because more than one vendor's config is TOML.</para>
/// </summary>
public static class TomlConfigFile {
    static readonly Lock              _writeLock = new();
    static readonly TomlTableTypeInfo _typeInfo  = new();

    public enum Outcome { Unchanged, Updated, Failed }

    /// <summary>Reads the file into a <see cref="TomlTable"/> for inspection. Null when the file is
    /// missing, unreadable, or not valid TOML — callers treat all three as "nothing of ours here".
    /// Shared read: the vendor writes this file itself, and a write-denying open would lock it out
    /// of its own config on Windows.</summary>
    public static TomlTable? Read(string path) {
        if (!File.Exists(path)) return null;
        try {
            return TomlSerializer.Deserialize(File.ReadAllTextShared(path), _typeInfo.TableInfo);
        } catch {
            return null;
        }
    }

    /// <summary>Load → <paramref name="mutate"/> (returns true when it changed <c>root</c>) →
    /// atomic write. A missing file starts from an empty table; a parse error returns
    /// <see cref="Outcome.Failed"/> rather than clobbering a config we cannot read.</summary>
    public static Outcome Edit(string path, Func<TomlTable, bool> mutate) {
        lock (_writeLock) {
            IDisposable crossProcess;
            try {
                path = Canonical(path);
                crossProcess = AcquireLock(path);
            } catch {
                return Outcome.Failed;
            }

            using (crossProcess) {
                TomlTable root;
                if (File.Exists(path)) {
                    try {
                        root = TomlSerializer.Deserialize(File.ReadAllText(path), _typeInfo.TableInfo) ?? new TomlTable();
                    } catch {
                        return Outcome.Failed;
                    }
                } else {
                    root = new TomlTable();
                }

                bool changed;
                try {
                    changed = mutate(root);
                } catch {
                    return Outcome.Failed;
                }

                if (!changed) return Outcome.Unchanged;

                try {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    WriteAtomic(path, root);
                    return Outcome.Updated;
                } catch {
                    return Outcome.Failed;
                }
            }
        }
    }

    /// <summary>Fetches an existing child table or adds a fresh one under <paramref name="key"/>.</summary>
    public static TomlTable GetOrAddTable(TomlTable parent, string key) {
        if (parent.TryGetValue(key, out var existing) && existing is TomlTable table) return table;
        var created = new TomlTable();
        parent[key] = created;
        return created;
    }

    /// <summary>A TOML array from string values. Collection expressions trip AOT on JsonArray; the
    /// same caution applies here, so build it by hand.</summary>
    public static TomlArray StringArray(IEnumerable<string> values) {
        var arr = new TomlArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    static void WriteAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try {
            WriteOwnerOnlyTemp(tmp, TomlSerializer.Serialize(root, _typeInfo.TableInfo));
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    static void WriteOwnerOnlyTemp(string path, string content) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share  = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            writer.Write(content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static string Canonical(string path) {
        var full = Path.GetFullPath(path);
        RejectSymlinkComponents(full);
        return full;
    }

    static void RejectSymlinkComponents(string path) {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException($"Path has no root: {path}");
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to update symlinked TOML configuration target: {current}");
        }
    }

    static IDisposable AcquireLock(string canonicalPath) {
        var dir = Path.GetDirectoryName(canonicalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return ConfigFileLock.Acquire(canonicalPath);
    }

    /// <summary>AOT-safe accessor for the built-in untyped-model metadata.
    /// <see cref="TomlSerializerContext.GetBuiltInTypeInfo{T}"/> is protected, so a thin subclass
    /// surfaces it without reflection.</summary>
    sealed class TomlTableTypeInfo : TomlSerializerContext {
        static readonly TomlSerializerOptions DefaultOptions = new();

        public readonly TomlTypeInfo<TomlTable> TableInfo = GetBuiltInTypeInfo<TomlTable>(DefaultOptions)!;

        public override TomlTypeInfo? GetTypeInfo(Type type, TomlSerializerOptions options) =>
            type == typeof(TomlTable) ? TableInfo : null;
    }
}
