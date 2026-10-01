using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Capacitor.Cli.Core.Config;

/// <summary>The persisted list of repo paths (<c>repos.json</c>) under the <see cref="ConfigRoot"/>
/// it is handed. Writes are atomic (temp + rename) so a reader never observes a partial file, and are
/// serialised across processes.</summary>
public sealed class RepoPathStore(ConfigRoot config, TimeProvider time) {
    const string StoreFileName = "repos.json";

    string StorePath { get; } = config.Path(StoreFileName);

    public static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The saved repositories; empty when there is no file, or when it cannot be read or parsed.
    /// Writers never act on that empty answer — see <see cref="ReadForWrite"/>.</summary>
    public Task<RepoEntry[]> LoadAsync() => Task.Run(() => Read() is { Entries: { } entries } ? entries : []);

    enum ReadStatus { Missing, Ok, Corrupt, Unreadable }

    sealed record ReadResult(ReadStatus Status, RepoEntry[]? Entries);

    // Windows fails a read while another process renames over the file; that clears in milliseconds.
    const int ReadAttempts = 25;
    static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(20);

    ReadResult Read() {
        for (var attempt = 1; ; attempt++) {
            // Absence is taken only from the open failing as not-found: File.Exists also answers false
            // when the check itself fails, which would let a write replace a store it never read.
            try {
                var json = File.ReadAllText(StorePath);
                RepoEntry[] parsed;
                try {
                    // A literal `null` is not a list either.
                    parsed = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.RepoEntryArray) ?? throw new JsonException("not a list");
                } catch (JsonException) {
                    return new(ReadStatus.Corrupt, null);
                }
                // An entry that parses but names no usable path is as unreadable as malformed JSON.
                if (parsed.Any(e => e is null || string.IsNullOrWhiteSpace(e.Path))) return new(ReadStatus.Corrupt, null);
                try {
                    return new(ReadStatus.Ok, Collapse(parsed));
                } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
                    return new(ReadStatus.Corrupt, null);
                }
            } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
                return new(ReadStatus.Missing, []);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                if (attempt == ReadAttempts) return new(ReadStatus.Unreadable, null);
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>The list a write starts from. A file that exists but cannot be read is never treated as
    /// empty — writing over it would replace every saved repository with the one being added. A file
    /// that reads but does not parse is moved aside, so its contents stay recoverable, and the write
    /// starts fresh.</summary>
    List<RepoEntry> ReadForWrite() {
        var read = Read();
        switch (read.Status) {
            case ReadStatus.Ok or ReadStatus.Missing:
                return [..read.Entries!];
            case ReadStatus.Corrupt:
                File.Move(StorePath, $"{StorePath}.corrupt-{time.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}", overwrite: false);
                return [];
            default:
                throw new IOException($"Could not read {StorePath}; leaving it unchanged rather than overwriting the saved repositories.");
        }
    }

    /// Worktree entries written before AddAsync resolved them (GH #655) collapse on read into
    /// their main repository, newest last_used winning — so historical pollution disappears from
    /// every consumer without a migration, and the next write persists the cleaned list.
    static RepoEntry[] Collapse(RepoEntry[] entries) {
        if (entries.Length == 0) return entries;

        var comparer = PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var byRepo = new Dictionary<string, RepoEntry>(comparer);
        foreach (var entry in entries) {
            var resolved = NormalizePath(GitRepository.ResolveMainRepoRoot(entry.Path));
            if (!byRepo.TryGetValue(resolved, out var existing) || entry.LastUsed > existing.LastUsed)
                byRepo[resolved] = entry with { Path = resolved };
        }

        return [..byRepo.Values];
    }

    public Task AddAsync(string path) {
        // A linked worktree registers as its main repository: user-facing repo lists show actual
        // repositories, and review flows launching into a requester's worktree must not mint a
        // "known repo" out of it (GH #655).
        var normalized = NormalizePath(GitRepository.ResolveMainRepoRoot(path));

        return WriteLocked(entries => {
            var existing = entries.FindIndex(e => string.Equals(e.Path, normalized, PathComparison));

            if (existing >= 0) {
                entries[existing] = entries[existing] with { LastUsed = time.GetUtcNow() };
            } else {
                entries.Add(new RepoEntry { Path = normalized, LastUsed = time.GetUtcNow() });
            }

            return true;
        });
    }

    public Task<bool> RemoveAsync(string path) {
        var normalized = NormalizePath(path);

        return WriteLocked(entries => entries.RemoveAll(e => string.Equals(e.Path, normalized, PathComparison)) > 0);
    }

    /// <summary>Read, change and save under the root's cross-process lock, so the daemon, the CLI and a
    /// second daemon sharing this root never interleave. The lock is a thread-affine mutex, so the whole
    /// body runs synchronously on one thread.</summary>
    Task<bool> WriteLocked(Func<List<RepoEntry>, bool> change) => Task.Run(() => {
        using (config.AcquireLock(StoreFileName)) {
            var entries = ReadForWrite();
            if (!change(entries)) return false;
            Save(entries);
            return true;
        }
    });

    void Save(List<RepoEntry> entries) {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $"repos.{Environment.ProcessId}.tmp");
        var sorted   = entries.OrderByDescending(e => e.LastUsed).ToArray();

        // Flushed to disk before the rename: NTFS can otherwise leave the renamed file empty or
        // zero-filled after a power loss.
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write)) {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(sorted, CapacitorJsonContext.Default.RepoEntryArray));
            stream.Flush(flushToDisk: true);
        }

        // Windows denies the replace while a reader holds the file without FILE_SHARE_DELETE; readers
        // are short-lived, so retry briefly before surfacing.
        for (var attempt = 1; ; attempt++) {
            try {
                File.Move(tempPath, StorePath, overwrite: true);
                return;
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < ReadAttempts) {
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    /// <summary>
    /// Returns all persisted repo paths sorted by last_used descending.
    /// </summary>
    public async Task<string[]> GetSortedPathsAsync() {
        var entries = await LoadAsync();
        return entries.OrderByDescending(e => e.LastUsed).Select(e => e.Path).ToArray();
    }

    /// <summary>Null when the file does not exist. Content rather than size and mtime: re-adding a
    /// known path rewrites the file at the same length, and two such writes inside the filesystem's
    /// timestamp resolution would otherwise read as one. Saves rename a complete file into place, so
    /// a fingerprint never describes a partial write.</summary>
    public RepoStoreFingerprint? Fingerprint() {
        try {
            // Shares delete so a save in another process can rename over the file mid-read.
            using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new RepoStoreFingerprint(Convert.ToHexStringLower(SHA256.HashData(stream)));
        } catch {
            return null;
        }
    }
}
