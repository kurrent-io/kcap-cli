using System.Security.Cryptography;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The built-in model sets under <c>models/</c>, each in a directory named by its set id.</summary>
static class LocalModelSets {
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

                using var stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            } catch (IOException) {
                return false;
            } catch (UnauthorizedAccessException) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Deletes every model set but <paramref name="keep"/>, and what an interrupted download left. Only
    /// directories named like a model set or a download's own leftover are touched, and no link is followed, so nothing
    /// outside <c>models/</c> is reached. Call it under the lifecycle lease, which every download holds. Never throws.</summary>
    public static void RemoveAllBut(LocalRoot root, LocalEmbeddingModelManifest keep) {
        try {
            var models = new DirectoryInfo(Directory(root));
            if (!models.Exists || models.LinkTarget is not null) return;

            foreach (var set in models.EnumerateDirectories()) {
                if (set.LinkTarget is not null || !(ReleaseManifest.IsValidSha256(set.Name) || IsLeftover(set.Name))
                 || string.Equals(set.Name, keep.ModelSet, StringComparison.OrdinalIgnoreCase)) continue;

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
