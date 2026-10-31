using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The built-in model sets under <c>models/</c>, each in a directory named by its set id.</summary>
public static class LocalModelSets {
    public const string PartialPrefix = ".partial-";
    public const string StalePrefix   = ".stale-";

    public static string Directory(LocalRoot root) => Path.Combine(root.Directory, "models");

    public static string SetDirectory(LocalRoot root, LocalEmbeddingModelManifest m) => Path.Combine(Directory(root), m.ModelSet);

    /// <summary>Every file is there with the declared size and digest.</summary>
    public static bool IsPresent(LocalRoot root, LocalEmbeddingModelManifest m) {
        var dir = SetDirectory(root, m);

        foreach (var file in m.Files) {
            var path = Path.Combine(dir, file.Name);

            try {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Size) return false;

                // Startup verification must not prevent an update from replacing the model set.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            } catch (IOException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The model set the installed bundle of <paramref name="version"/> carries beside its server, or null when
    /// it carries none or it cannot be read.</summary>
    public static LocalEmbeddingModelManifest? Bundled(LocalRoot root, string version) {
        try {
            return LocalEmbeddingModelManifest.Parse(JsonNode.Parse(File.ReadAllBytes(Path.Combine(root.Directory, "bin", version, "embedding-model.json"))));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>Deletes every model set not named in <paramref name="keep"/>, and what an interrupted download left. Only
    /// directories named like a model set or a download's own leftover are touched, and no link is followed, so nothing
    /// outside <c>models/</c> is reached. Call it under the lifecycle lease, which every download holds. Never throws.</summary>
    public static void RemoveAllBut(LocalRoot root, params string?[] keep) {
        try {
            var models = new DirectoryInfo(Directory(root));
            if (!models.Exists || models.LinkTarget is not null) return;

            foreach (var set in models.EnumerateDirectories()) {
                if (set.LinkTarget is not null || !(ReleaseManifest.IsValidSha256(set.Name) || IsLeftover(set.Name))
                 || keep.Any(k => string.Equals(set.Name, k, StringComparison.OrdinalIgnoreCase))) continue;

                try {
                    set.Delete(recursive: true);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    // The next upgrade tries again.
                }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // models/ could not be listed; nothing was removed.
        }
    }

    static bool IsLeftover(string name) =>
        name.StartsWith(PartialPrefix, StringComparison.Ordinal) || name.StartsWith(StalePrefix, StringComparison.Ordinal);
}
