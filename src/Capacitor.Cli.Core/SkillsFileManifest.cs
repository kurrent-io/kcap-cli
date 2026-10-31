using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core;

/// <summary>
/// The hash of every file <see cref="AgentsSkillsInstaller"/> wrote into a skills tree, kept beside the
/// skills so a removal can tell a copy kcap still owns from one the user edited or added files to.
/// </summary>
static class SkillsFileManifest {
    public const string FileName = ".kcap-skills.json";

    public enum State { Absent, Unreadable, Present }

    public static void Write(string targetDir, IEnumerable<string> folders) {
        var files = new JsonObject();

        foreach (var folder in folders) {
            var root = Path.Combine(targetDir, folder);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                files[Key(targetDir, file)] = JsonValue.Create(Hash(file));
        }

        AtomicFile.Replace(Path.Combine(targetDir, FileName), new JsonObject { ["files"] = files }.ToJsonString());
    }

    public static State Read(string targetDir, out IReadOnlyDictionary<string, string> files) {
        files = new Dictionary<string, string>();
        var path = Path.Combine(targetDir, FileName);

        if (!File.Exists(path)) return State.Absent;

        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.Obj("files") is not { } entries) return State.Unreadable;

            var read = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateObject()) {
                if (!entry.Value.IsString) return State.Unreadable;
                read[entry.Name] = entry.Value.GetString()!;
            }

            files = read;

            return State.Present;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return State.Unreadable;
        }
    }

    /// <summary>True when <paramref name="folder"/> holds exactly the files the manifest recorded for it,
    /// byte for byte. A folder the manifest never recorded is not provably kcap's.</summary>
    public static bool IsPristine(string targetDir, string folder, IReadOnlyDictionary<string, string> files) {
        var prefix   = folder + "/";
        var expected = files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(StringComparer.Ordinal);

        if (expected.Count == 0) return false;

        try {
            var seen = 0;

            foreach (var file in Directory.EnumerateFiles(Path.Combine(targetDir, folder), "*", SearchOption.AllDirectories)) {
                if (!expected.TryGetValue(Key(targetDir, file), out var hash) || Hash(file) != hash) return false;
                seen++;
            }

            return seen == expected.Count;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    public static void Delete(string targetDir) {
        try { File.Delete(Path.Combine(targetDir, FileName)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A stale manifest only makes a later removal more careful.
        }
    }

    static string Key(string targetDir, string file) =>
        Path.GetRelativePath(targetDir, file).Replace(Path.DirectorySeparatorChar, '/');

    static string Hash(string file) {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
