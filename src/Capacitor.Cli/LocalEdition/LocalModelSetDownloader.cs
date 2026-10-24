using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Fetches a model set into <c>models/&lt;model_set&gt;</c>. Every file is verified in a partial directory
/// first, and the directory is moved into place whole, so a set is either complete or absent.</summary>
public sealed class LocalModelSetDownloader(LocalRoot root, LocalBundleClient bundles) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    string Models => Path.Combine(root.Directory, "models");

    /// <summary>This instance put a model set in place.</summary>
    public bool Fetched { get; private set; }

    string SetDirectory(LocalEmbeddingModelManifest m) => Path.Combine(Models, m.ModelSet);

    /// <summary>Every file is there with the declared size and digest.</summary>
    public bool IsPresent(LocalEmbeddingModelManifest m) {
        var dir = SetDirectory(m);

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

    /// <exception cref="LocalModelDownloadException">The publication lacks a file, the network failed or timed out,
    /// or a file is not what the manifest signed.</exception>
    public async Task DownloadAsync(LocalEmbeddingModelManifest m, IProgress<(long Received, long Total)> progress, CancellationToken ct) {
        if (IsPresent(m)) return;

        var partial = Path.Combine(Models, $".partial-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        var total   = m.TotalSize;

        try {
            DurableDirectory.Ensure(partial, root.Directory, OwnerOnly);

            long done = 0;

            foreach (var file in m.Files) {
                var before = done;

                await bundles.DownloadModelFileAsync(m.ModelSet, file, Path.Combine(partial, file.Name),
                    new SyncProgress<long>(received => progress.Report((before + received, total))), ct);

                done += file.Size;
            }

            Install(m, partial);
            Fetched = true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new LocalModelDownloadException(LocalModelDownloadCode.DownloadFailed, $"Could not store the model set: {e.Message}", e);
        } finally {
            try {
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Cleanup must never replace the exception in flight. The next download uses a fresh name; a leftover is only disk.
            }
        }
    }

    /// <summary>Deletes every model set but <paramref name="keep"/>, and what an interrupted download left. Only
    /// directories named like a model set or a download's own leftover are touched, and no link is followed, so nothing
    /// outside <c>models/</c> is reached. Call it under the lifecycle lease, which every download holds. Never throws.</summary>
    public void RemoveAllBut(LocalEmbeddingModelManifest keep) {
        try {
            var models = new DirectoryInfo(Models);
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
        name.StartsWith(".partial-", StringComparison.Ordinal) || name.StartsWith(".stale-", StringComparison.Ordinal);

    /// <summary>An invalid set already in place is renamed aside rather than deleted first, so a crash never loses
    /// a good set; a move that loses a race to a download that produced a valid set is a success.</summary>
    void Install(LocalEmbeddingModelManifest m, string partial) {
        var target = SetDirectory(m);
        var stale  = Path.Combine(Models, $".stale-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");

        try {
            if (Directory.Exists(target)) Directory.Move(target, stale);

            try {
                Directory.Move(partial, target);
            } catch (IOException) when (IsPresent(m)) {
                return;
            }

            DurableDirectory.Sync(Models);
        } finally {
            try {
                if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // A leftover stale directory is only disk.
            }
        }
    }
}
