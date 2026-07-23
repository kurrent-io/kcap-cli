using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed partial class SessionStartMemoryLeaseStore {
    const string MetadataName = "store-meta-v1.json";
    static readonly TimeSpan NormalSweepInterval = TimeSpan.FromMinutes(15);

    readonly string _root;
    readonly string _lockPath;
    readonly string _metadataPath;
    readonly TimeProvider _time;
    readonly Action<string>? _diagnostic;

    public SessionStartMemoryLeaseStore(string? root = null, TimeProvider? time = null, Action<string>? diagnostic = null) {
        _root = SessionStartMemoryStorePaths.ValidateRoot(root ?? SessionStartMemoryStorePaths.DefaultRoot);
        _lockPath = Path.Combine(_root, "store.lock");
        _metadataPath = Path.Combine(_root, MetadataName);
        _time = time ?? TimeProvider.System;
        _diagnostic = diagnostic;
    }

    public async Task<SessionStartMemoryLeaseHandle?> TryBeginAsync(
        string key, TimeSpan budget, CancellationToken ct = default) {
        if (!KeyRegex().IsMatch(key) || budget <= TimeSpan.Zero) return null;
        try {
            using var gate = await AcquireAsync(budget, ct);
            if (gate is null) return null;
            EnsureSafeRoot();
            SweepUnderLock(TimeSpan.FromMilliseconds(25), 5_000, force: false);

            var path = RecordPath(key);
            var now = _time.GetUtcNow();
            var current = ReadRecord(path);
            if (current is not null) {
                if (!Validate(current)) return null;
                // Completion remains authoritative until a sweep physically removes the
                // tombstone. This makes the documented 30-day guarantee a lower bound even
                // when the opportunistic sweep is delayed by its 15-minute cadence.
                if (current.State == "completed") return null;
                if (current.State == "retry_pending" && current.NextAttemptAt is { } next && now < next)
                    return null;
                if (current.State == "leased" && current.LeaseExpiresAt is { } expiry && now < expiry)
                    return null;
            }

            if (!EnsureWriteCapacity(creatingRecord: current is null)) return null;
            var generation = checked((current?.Generation ?? 0) + 1);
            var attempt = current?.State == "retry_pending" ? checked(current.Attempt + 1) : current?.Attempt ?? 0;
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var nextRecord = new SessionStartMemoryStoreRecord(
                SessionStartMemoryConstants.SchemaVersion,
                SessionStartMemoryConstants.PolicyVersion,
                SessionStartMemoryConstants.FragmentVersion,
                "leased", generation, token, attempt,
                now + SessionStartMemoryConstants.LeaseDuration, null, null, null);
            WriteRecord(path, nextRecord);
            return new SessionStartMemoryLeaseHandle(key, generation, token);
        } catch (Exception ex) when (IsOperational(ex)) {
            _diagnostic?.Invoke($"SessionStart memory coordination unavailable: {ex.Message}");
            return null;
        }
    }

    public Task<bool> CompleteAsync(SessionStartMemoryLeaseHandle handle, SessionStartMemoryDisposition disposition,
        CancellationToken ct = default) => MutateAsync(handle, disposition, retryAfter: null, ct);

    public Task<bool> RetryAsync(SessionStartMemoryLeaseHandle handle, TimeSpan? retryAfter,
        CancellationToken ct = default) => MutateAsync(handle, SessionStartMemoryDisposition.RetryableFailure, retryAfter, ct);

    async Task<bool> MutateAsync(SessionStartMemoryLeaseHandle handle, SessionStartMemoryDisposition disposition,
        TimeSpan? retryAfter, CancellationToken ct) {
        try {
            using var gate = await AcquireAsync(TimeSpan.FromSeconds(2), ct);
            if (gate is null) return false;
            EnsureSafeRoot();
            var path = RecordPath(handle.Key);
            var current = ReadRecord(path);
            if (current is null || !Validate(current) || current.State != "leased" ||
                current.Generation != handle.Generation ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(current.Token ?? ""),
                    Encoding.ASCII.GetBytes(handle.Token))) return false;
            if (!EnsureWriteCapacity(creatingRecord: false)) return false;

            var now = _time.GetUtcNow();
            SessionStartMemoryStoreRecord next;
            if (disposition == SessionStartMemoryDisposition.RetryableFailure) {
                var scheduled = Backoff(current.Attempt);
                if (retryAfter is { } requested && requested > scheduled) scheduled = requested;
                if (scheduled > TimeSpan.FromHours(1)) scheduled = TimeSpan.FromHours(1);
                next = current with {
                    State = "retry_pending", Token = null, LeaseExpiresAt = null,
                    NextAttemptAt = now + scheduled, CompletedAt = null,
                    Disposition = "retryable_failure"
                };
            } else {
                next = current with {
                    State = "completed", Token = null, LeaseExpiresAt = null,
                    CompletedAt = now, NextAttemptAt = null,
                    Disposition = disposition == SessionStartMemoryDisposition.Ready
                        ? "ready"
                        : "complete_without_context"
                };
            }
            WriteRecord(path, next);
            return true;
        } catch (Exception ex) when (IsOperational(ex)) {
            _diagnostic?.Invoke($"SessionStart memory coordination unavailable: {ex.Message}");
            return false;
        }
    }

    public async Task SweepAsync(TimeSpan budget, int maxEntries = 5_000, CancellationToken ct = default) {
        if (budget <= TimeSpan.Zero || maxEntries <= 0) return;
        try {
            using var gate = await AcquireAsync(budget, ct);
            if (gate is null) return;
            EnsureSafeRoot();
            SweepUnderLock(budget, maxEntries, force: true);
        } catch (Exception ex) when (IsOperational(ex)) {
            _diagnostic?.Invoke($"SessionStart memory sweep skipped: {ex.Message}");
        }
    }

    void SweepUnderLock(TimeSpan budget, int maxEntries, bool force) {
        var now = _time.GetUtcNow();
        var metadata = ReadMetadata();
        if (!force && metadata.LastSweepAt is { } last && now >= last && now - last < NormalSweepInterval)
            return;

        var names = Directory.EnumerateFileSystemEntries(_root)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .Where(IsSweepCandidate)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var cursor = ValidateCursor(metadata.LastProcessedFilename) ? metadata.LastProcessedFilename : null;
        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        string? lastProcessed = null;
        var reachedEnd = true;

        foreach (var name in names) {
            if (cursor is not null && StringComparer.Ordinal.Compare(name, cursor) <= 0) continue;
            if (processed >= maxEntries || Stopwatch.GetElapsedTime(started) >= budget) {
                reachedEnd = false;
                break;
            }
            processed++;
            lastProcessed = name; // poison entries must never pin cleanup progress
            var path = Path.Combine(_root, name);
            try {
                if (RecordRegex().IsMatch(name)) {
                    var record = ReadRecord(path);
                    if (record is not null && Validate(record) && IsSweepExpired(record, now))
                        File.Delete(path);
                } else if (TempRegex().IsMatch(name) &&
                           now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) >= TimeSpan.FromDays(1)) {
                    File.Delete(path);
                }
            } catch (Exception ex) when (IsOperational(ex)) {
                _diagnostic?.Invoke($"SessionStart memory sweep retained {name}: {ex.Message}");
            }
        }

        // If the cursor was after every currently valid candidate, wrap now rather than pinning it.
        if (lastProcessed is null && cursor is not null) reachedEnd = true;
        var nextCursor = reachedEnd ? null : lastProcessed ?? cursor;
        WriteMetadata(new SessionStartMemoryStoreMetadata(
            SessionStartMemoryConstants.SchemaVersion, now, nextCursor));
    }

    SessionStartMemoryStoreMetadata ReadMetadata() {
        if (!File.Exists(_metadataPath))
            return new SessionStartMemoryStoreMetadata(SessionStartMemoryConstants.SchemaVersion, null, null);
        try {
            var value = BoundedJsonFile.Read(_metadataPath, SessionStartMemoryConstants.MaxMetadataBytes,
                SessionStartMemoryJsonContext.Default.SessionStartMemoryStoreMetadata);
            if (value is null || value.SchemaVersion != SessionStartMemoryConstants.SchemaVersion ||
                value.LastSweepAt is { } sweepAt && sweepAt.Offset != TimeSpan.Zero ||
                !ValidateCursor(value.LastProcessedFilename))
                throw new InvalidDataException("Invalid SessionStart memory sweep metadata.");
            return value;
        } catch (Exception ex) when (IsOperational(ex)) {
            _diagnostic?.Invoke($"SessionStart memory sweep metadata reset: {ex.Message}");
            return new SessionStartMemoryStoreMetadata(SessionStartMemoryConstants.SchemaVersion, null, null);
        }
    }

    void WriteMetadata(SessionStartMemoryStoreMetadata metadata) {
        if (CountChildren() >= SessionStartMemoryConstants.TotalEntryCap) {
            _diagnostic?.Invoke("SessionStart memory sweep cursor not persisted: store capacity reached.");
            return;
        }
        try {
            BoundedJsonFile.AtomicWrite(_root, _metadataPath, metadata, SessionStartMemoryConstants.MaxMetadataBytes,
                SessionStartMemoryJsonContext.Default.SessionStartMemoryStoreMetadata);
        } catch (Exception ex) when (IsOperational(ex)) {
            _diagnostic?.Invoke($"SessionStart memory sweep cursor not persisted: {ex.Message}");
        }
    }

    async Task<FileStream?> AcquireAsync(TimeSpan budget, CancellationToken ct) {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < budget && !ct.IsCancellationRequested) {
            try {
                var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                SetOwnerOnly(_lockPath);
                return stream;
            } catch (IOException) {
                var remaining = budget - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(5) ? remaining : TimeSpan.FromMilliseconds(5), ct);
            }
        }
        return null;
    }

    void EnsureSafeRoot() {
        var info = new DirectoryInfo(_root);
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("SessionStart memory store root is unsafe.");
    }

    string RecordPath(string key) {
        if (!KeyRegex().IsMatch(key)) throw new InvalidDataException("Invalid SessionStart memory key.");
        return Path.Combine(_root, key + ".json");
    }

    SessionStartMemoryStoreRecord? ReadRecord(string path) {
        if (!File.Exists(path)) return null;
        return BoundedJsonFile.Read(path, SessionStartMemoryConstants.MaxRecordBytes,
            SessionStartMemoryJsonContext.Default.SessionStartMemoryStoreRecord);
    }

    void WriteRecord(string path, SessionStartMemoryStoreRecord record) {
        if (!Validate(record)) throw new InvalidDataException("Invalid SessionStart memory record.");
        BoundedJsonFile.AtomicWrite(_root, path, record, SessionStartMemoryConstants.MaxRecordBytes,
            SessionStartMemoryJsonContext.Default.SessionStartMemoryStoreRecord);
    }

    bool EnsureWriteCapacity(bool creatingRecord) {
        var total = CountChildren();
        var records = Directory.EnumerateFiles(_root, "*.json")
            .Count(p => RecordRegex().IsMatch(Path.GetFileName(p)));
        if (total < SessionStartMemoryConstants.TotalEntryCap &&
            (!creatingRecord || records < SessionStartMemoryConstants.NormalRecordCap)) return true;

        SweepUnderLock(TimeSpan.FromMilliseconds(250), 10_000, force: true);
        total = CountChildren();
        records = Directory.EnumerateFiles(_root, "*.json")
            .Count(p => RecordRegex().IsMatch(Path.GetFileName(p)));
        var available = total < SessionStartMemoryConstants.TotalEntryCap &&
                        (!creatingRecord || records < SessionStartMemoryConstants.NormalRecordCap);
        if (!available) _diagnostic?.Invoke("SessionStart memory coordination unavailable: store capacity reached.");
        return available;
    }

    int CountChildren() => Directory.EnumerateFileSystemEntries(_root)
        .Count(p => Path.GetFileName(p) is not "store.lock" and not MetadataName);

    static bool Validate(SessionStartMemoryStoreRecord value) {
        if (value.SchemaVersion != SessionStartMemoryConstants.SchemaVersion ||
            value.PolicyVersion != SessionStartMemoryConstants.PolicyVersion ||
            value.FragmentVersion != SessionStartMemoryConstants.FragmentVersion ||
            value.Generation < 0 || value.Attempt < 0 ||
            value.State is not ("leased" or "completed" or "retry_pending") ||
            value.LeaseExpiresAt is { } leaseAt && leaseAt.Offset != TimeSpan.Zero ||
            value.CompletedAt is { } completedAt && completedAt.Offset != TimeSpan.Zero ||
            value.NextAttemptAt is { } nextAt && nextAt.Offset != TimeSpan.Zero) return false;
        return value.State switch {
            "leased" => value.Token is not null && TokenRegex().IsMatch(value.Token) &&
                        value.LeaseExpiresAt is not null && value.CompletedAt is null &&
                        value.NextAttemptAt is null && value.Disposition is null,
            "completed" => value.Token is null && value.LeaseExpiresAt is null &&
                           value.CompletedAt is not null && value.NextAttemptAt is null &&
                           value.Disposition is "ready" or "complete_without_context",
            "retry_pending" => value.Token is null && value.LeaseExpiresAt is null &&
                               value.CompletedAt is null && value.NextAttemptAt is not null &&
                               value.Disposition == "retryable_failure",
            _ => false
        };
    }

    static bool IsSweepExpired(SessionStartMemoryStoreRecord value, DateTimeOffset now) =>
        value.State == "completed" && value.CompletedAt is { } completed &&
        now - completed >= SessionStartMemoryConstants.Retention ||
        value.State == "retry_pending" && value.NextAttemptAt is { } retry &&
        now - retry >= SessionStartMemoryConstants.Retention;

    static TimeSpan Backoff(long attempt) => attempt switch {
        <= 0 => TimeSpan.FromSeconds(5),
        1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10)
    };

    static bool IsSweepCandidate(string name) => RecordRegex().IsMatch(name) || TempRegex().IsMatch(name);
    static bool ValidateCursor(string? value) => value is null ||
        value.Length <= 128 && value.All(static c => c <= 0x7f) && IsSweepCandidate(value);

    static void SetOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    static bool IsOperational(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or OperationCanceledException or OverflowException or
        ArgumentException or NotSupportedException;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex KeyRegex();
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex TokenRegex();
    [GeneratedRegex("^[0-9a-f]{64}\\.json$", RegexOptions.CultureInvariant)] private static partial Regex RecordRegex();
    [GeneratedRegex("^[0-9a-f]{64}\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant)] private static partial Regex TempRegex();
}
