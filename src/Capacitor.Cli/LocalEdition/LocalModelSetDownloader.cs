using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Fetches a model set into <c>models/&lt;model_set&gt;</c>. Every file is verified in a partial directory
/// first, and the directory is moved into place whole, so a set is either complete or absent.</summary>
public sealed class LocalModelSetDownloader(LocalRoot root, LocalBundleClient bundles) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    string Models => LocalModelSets.Directory(root);

    /// <summary>This instance put a model set in place.</summary>
    public bool Fetched { get; private set; }

    /// <summary>Runs once every file of a download has verified, before the set is moved into place; false leaves
    /// the download unpublished.</summary>
    public Func<LocalEmbeddingModelManifest, bool>? Publishing { get; init; }

    /// <summary>Every file is there with the declared size and digest.</summary>
    public bool IsPresent(LocalEmbeddingModelManifest m) => LocalModelSets.IsPresent(root, m);

    /// <exception cref="LocalModelDownloadException">The publication lacks a file, the network failed or timed out,
    /// or a file is not what the manifest signed.</exception>
    /// <returns>False when <see cref="Publishing"/> refused the download.</returns>
    public async Task<bool> DownloadAsync(LocalEmbeddingModelManifest m, IProgress<(long Received, long Total)> progress, CancellationToken ct) {
        if (IsPresent(m)) return true;

        var partial = Path.Combine(Models, $"{LocalModelSets.PartialPrefix}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
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

            ct.ThrowIfCancellationRequested();

            if (Publishing?.Invoke(m) == false) return false;

            Install(m, partial);
            Fetched = true;

            return true;
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

    /// <summary>An invalid set already in place is renamed aside rather than deleted first, so a crash never loses
    /// a good set; a move that loses a race to a download that produced a valid set is a success.</summary>
    void Install(LocalEmbeddingModelManifest m, string partial) {
        var target = LocalModelSets.SetDirectory(root, m);
        var stale  = Path.Combine(Models, $"{LocalModelSets.StalePrefix}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");

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
