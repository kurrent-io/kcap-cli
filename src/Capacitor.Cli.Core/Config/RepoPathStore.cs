using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Capacitor.Cli.Core.Config;

/// <summary>The persisted list of repo paths (<c>repos.json</c>) under the <see cref="ConfigRoot"/>
/// it is handed. Writes are atomic (temp + rename) so a reader never observes a partial file.</summary>
public sealed class RepoPathStore(ConfigRoot config, TimeProvider time) {
    string StorePath { get; } = config.Path("repos.json");

    // The file each save replaces, kept so a repos.json that reads back unparseable — a crash or power
    // loss between rename and flush can leave one zero-filled — still has a last good copy.
    string BackupPath { get; } = config.Path("repos.json.bak");

    // Static: serialises the read-modify-write for the whole process however many instances exist.
    static readonly SemaphoreSlim Lock = new(1, 1);

    public static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    // A root keeps its separator: `C:` names the current directory on drive C, not its root.
    internal static string NormalizePath(string path) {
        var full = Path.GetFullPath(path);
        return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// Empty when the list cannot be read. For display and matching only — a write must never start
    /// from this, since saving it would wipe every repository the unreadable file still holds.
    public async Task<RepoEntry[]> LoadAsync() => await TryLoadAsync() ?? [];

    /// Null when repos.json exists but neither it nor its backup can be read; empty only when there is
    /// genuinely no list yet.
    public async Task<RepoEntry[]?> TryLoadAsync() {
        if (!File.Exists(StorePath))
            return [];

        return await TryReadAsync(StorePath) ?? await TryReadAsync(BackupPath);
    }

    async Task<RepoEntry[]?> TryReadAsync(string path) {
        for (var attempt = 0; ; attempt++) {
            try {
                // Shares write and delete: on Windows a reader holding the file without them makes
                // another process's save fail at the rename.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entries = await JsonSerializer.DeserializeAsync(stream, CapacitorJsonContext.Default.RepoEntryArray);
                return entries is null ? null : Collapse(entries);
            } catch (FileNotFoundException) {
                return null;
            } catch (DirectoryNotFoundException) {
                return null;
            } catch (IOException) when (attempt < 4) {
                await Task.Delay(TimeSpan.FromMilliseconds(50), time);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                return null;
            }
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

    public async Task AddAsync(string path) {
        // A linked worktree registers as its main repository: user-facing repo lists show actual
        // repositories, and review flows launching into a requester's worktree must not mint a
        // "known repo" out of it (GH #655).
        var normalized = NormalizePath(GitRepository.ResolveMainRepoRoot(path));

        await Lock.WaitAsync();

        try {
            var entries  = (await LoadForWriteAsync()).ToList();
            var existing = entries.FindIndex(e => string.Equals(e.Path, normalized, PathComparison));

            if (existing >= 0) {
                entries[existing] = entries[existing] with { LastUsed = time.GetUtcNow() };
            } else {
                entries.Add(new RepoEntry { Path = normalized, LastUsed = time.GetUtcNow() });
            }

            await SaveAsync(entries);
        } finally {
            Lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string path) {
        var normalized = NormalizePath(path);

        await Lock.WaitAsync();

        try {
            var entries = (await LoadForWriteAsync()).ToList();
            var removed = entries.RemoveAll(e => string.Equals(e.Path, normalized, PathComparison));

            if (removed == 0) return false;

            await SaveAsync(entries);
            return true;
        } finally {
            Lock.Release();
        }
    }

    async Task<RepoEntry[]> LoadForWriteAsync() =>
        await TryLoadAsync()
     ?? throw new IOException($"{StorePath} cannot be read and has no readable backup; it is left untouched rather than overwritten.");

    async Task SaveAsync(List<RepoEntry> entries) {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $"repos.{Environment.ProcessId}.tmp");
        var sorted   = entries.OrderByDescending(e => e.LastUsed).ToArray();

        // Flushed to disk before the swap: NTFS can persist the rename ahead of the data, so a power
        // loss right after an unflushed save leaves repos.json zero-filled.
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
            await JsonSerializer.SerializeAsync(stream, sorted, CapacitorJsonContext.Default.RepoEntryArray);
            stream.Flush(flushToDisk: true);
        }

        for (var attempt = 0; ; attempt++) {
            try {
                if (File.Exists(StorePath)) File.Replace(tempPath, StorePath, BackupPath, ignoreMetadataErrors: true);
                else File.Move(tempPath, StorePath);
                return;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 9) {
                // Windows refuses the swap while another process holds repos.json without delete sharing.
                await Task.Delay(TimeSpan.FromMilliseconds(50), time);
            }
        }
    }

    /// <summary>
    /// Returns all persisted repo paths sorted by last_used descending.
    /// </summary>
    public async Task<string[]> GetSortedPathsAsync() => await TryGetSortedPathsAsync() ?? [];

    /// Null when the list cannot be read — see <see cref="TryLoadAsync"/>.
    public async Task<string[]?> TryGetSortedPathsAsync() =>
        (await TryLoadAsync())?.OrderByDescending(e => e.LastUsed).Select(e => e.Path).ToArray();

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
