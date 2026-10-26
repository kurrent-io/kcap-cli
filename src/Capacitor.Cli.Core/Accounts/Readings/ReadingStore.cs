using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>One reading file per account under <c>readings/</c> beside <c>accounts.json</c>. Producers
/// take only <c>readings.lock</c>; a registry change that must agree with the readings takes
/// <c>accounts.lock</c> first, then this lock, through <see cref="AccountStore.MutateWithReadings{T}"/>.</summary>
public sealed class ReadingStore {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDir  = OwnerOnlyFile | UnixFileMode.UserExecute;

    readonly string                  _lockPath;
    readonly Func<AccountRegistry?>  _registry;
    readonly Action                  _ensureParent;

    internal ReadingStore(string directory, string lockPath, Func<AccountRegistry?> registry, Action ensureParent) {
        Directory     = directory;
        _lockPath     = lockPath;
        _registry     = registry;
        _ensureParent = ensureParent;
    }

    public string Directory { get; }

    // Seam: a test runs a removal here, after the membership check and before the write.
    internal Action? AfterMembershipCheck { get; set; }

    // Seam: a test that is not about contention widens it, so a loaded machine cannot skip its write.
    internal TimeSpan ProducerBudget { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Null when the lock is contended past <paramref name="budget"/> or cannot be opened.</summary>
    public IDisposable? TryLock(TimeSpan budget) {
        try {
            return ConfigFileLock.Acquire(_lockPath, budget);
        } catch (Exception ex) when (IsLockFailure(ex)) {
            return null;
        }
    }

    internal IDisposable Lock() => ConfigFileLock.Acquire(_lockPath);

    /// <summary>Writes nothing, and returns false, when the account is not registered at
    /// <paramref name="producerGeneration"/> or <paramref name="sessionId"/> was retired from it. Never
    /// throws for IO, lock or parse failures. Not re-entrant: never call it while holding
    /// <c>readings.lock</c>.</summary>
    public bool TryRecord(string accountId, long producerGeneration, string? sessionId, Func<ReadingFile, ReadingFile> update) {
        if (!SafeId(accountId)) return false;

        try {
            using var lease = TryLock(ProducerBudget);
            if (lease is null) return false;

            var account = _registry()?.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null || account.Generation != producerGeneration) return false;
            if (sessionId is not null && account.RetiredSessions?.Contains(sessionId) == true) return false;

            AfterMembershipCheck?.Invoke();

            WriteFile(accountId, update(ReadNormalized(accountId, account.Generation)));
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || IsLockFailure(ex)) {
            return false;
        }
    }

    /// <summary>Unreadable or unparseable files are skipped.</summary>
    public IReadOnlyDictionary<string, ReadingFile> LoadAll() {
        var files = ReadingPaths();
        if (files.Length == 0) return FrozenDictionary<string, ReadingFile>.Empty;

        var all = new Dictionary<string, ReadingFile>(StringComparer.Ordinal);
        foreach (var path in files) {
            try {
                if (Parse(ReadShared(path)) is { } file) all[Path.GetFileNameWithoutExtension(path)] = file;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Removed or replaced while listing.
            }
        }

        return all;
    }

    /// <summary>Account id, length and last-write ticks of every reading file in name order, for change detection.</summary>
    public (string AccountId, long Length, long Ticks)[] Stat() {
        var stats = new List<(string, long, long)>();
        foreach (var path in ReadingPaths()) {
            try {
                var info = new FileInfo(path);
                if (info.Exists) stats.Add((Path.GetFileNameWithoutExtension(path), info.Length, info.LastWriteTimeUtc.Ticks));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Removed while listing.
            }
        }

        return [.. stats];
    }

    internal ReadingFile ReadNormalized(string accountId, long generation) {
        var path = PathFor(accountId);
        if (!File.Exists(path)) return ReadingFile.Empty(generation);

        return Parse(ReadShared(path)) is { } file && file.Reading.Generation == generation
            ? file
            : ReadingFile.Empty(generation);
    }

    internal void WriteFile(string accountId, ReadingFile file) {
        var path = PathFor(accountId);
        EnsureDirectory();
        AtomicFile.Replace(path, JsonSerializer.Serialize(file, ReadingsJsonContext.Default.ReadingFile), OwnerOnlyFile, followLink: false);
    }

    internal void DeleteFile(string accountId) {
        if (SafeId(accountId)) File.Delete(PathFor(accountId));
    }

    string PathFor(string accountId) =>
        SafeId(accountId)
            ? Path.Combine(Directory, accountId + ".json")
            : throw new ArgumentException($"'{accountId}' cannot name a reading file.", nameof(accountId));

    // Ids are kcap-generated, but a hand-edited registry must not steer a write outside the directory.
    static bool SafeId(string accountId) =>
        accountId is { Length: > 0 } and not ("." or "..")
     && accountId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
     && accountId.IndexOfAny(['/', '\\']) < 0;

    string[] ReadingPaths() {
        try {
            if (!System.IO.Directory.Exists(Directory)) return [];

            var paths = System.IO.Directory.GetFiles(Directory, "*.json");
            Array.Sort(paths, StringComparer.Ordinal);
            return paths;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static ReadingFile? Parse(string json) {
        try {
            return JsonSerializer.Deserialize(json, ReadingsJsonContext.Default.ReadingFile) is { } file && Complete(file) ? file : null;
        } catch (JsonException) {
            return null;
        }
    }

    // Source-generated binding leaves a missing member, or a null array element, null whatever its annotation says.
    static bool Complete(ReadingFile file) =>
        file is { Reading.Windows: { } windows, Sessions: { } sessions }
     && windows.All(w => w?.LimitId is not null)
     && sessions.All(s => s?.SessionId is not null)
     && (file.Reading.Blocked is not { } blocked || blocked.Constraints?.All(c => c is not null) == true);

    // Read beside a concurrent atomic replace: a share mode that denies writers would fail the rename on Windows.
    static string ReadShared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static bool IsLockFailure(Exception ex) => ex is TimeoutException or WaitHandleCannotBeOpenedException or IOException or UnauthorizedAccessException;

    void EnsureDirectory() {
        _ensureParent();
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
        else {
            System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDir);
            File.SetUnixFileMode(Directory, OwnerOnlyDir);
        }
    }
}
