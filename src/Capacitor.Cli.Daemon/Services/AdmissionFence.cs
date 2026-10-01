using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The one boundary between admitting new work and retiring this daemon for a rename. Every entry
/// point that creates daemon-owned work admits through <see cref="TryAdmit"/>; a rename acquires the
/// fence only while nothing is in flight and the daemon is idle, under the same lock, so the idle check
/// and the admission stop are one step.
/// </summary>
/// <remarks>
/// A held fence belongs to one control connection and ends with it. A committed fence outlives the
/// connection — the CLI may be mid-bootout — and is persisted, so a replacement process started under
/// this name before the bootout lands inherits it. Only <see cref="CommitLease"/> or an explicit
/// abort on the committing connection lifts it.
/// </remarks>
internal sealed partial class AdmissionFence {
    /// <summary>Outlasts a bootout and its stop confirmation, which the CLI runs synchronously within
    /// its own 20s retire budget; a daemon still running after it was not retired.</summary>
    public static readonly TimeSpan CommitLease = TimeSpan.FromMinutes(2);

    readonly object _gate = new();
    readonly string _markerPath;
    readonly string? _instanceId;
    readonly TimeProvider _time;
    readonly ILogger _log;

    int _inFlight;
    Hold? _held;
    DateTimeOffset? _committedAt;

    public AdmissionFence(string markerPath, string? instanceId, TimeProvider time, ILogger<AdmissionFence> logger) {
        _markerPath = markerPath;
        _instanceId = instanceId;
        _time = time;
        _log = logger;
        _committedAt = ReadMarker();
    }

    public enum AcquireResult { Acquired, Busy, Fenced }

    /// <summary>True while a fence is held or committed — new work is refused.</summary>
    public bool IsFenced {
        get { lock (_gate) return FencedLocked(); }
    }

    /// <summary>Admits one unit of new work, or returns null while fenced. Dispose the admission when
    /// the entry point returns; by then admitted work is visible to the busy check.</summary>
    public IDisposable? TryAdmit() {
        lock (_gate) {
            if (FencedLocked()) return null;
            _inFlight++;
            return new Admission(this);
        }
    }

    /// <summary>Counts work that continues a run admitted earlier — it is never refused, but while it
    /// runs the daemon is busy, even if its run has already left the cache.</summary>
    public IDisposable Track() {
        lock (_gate) {
            _inFlight++;
            return new Admission(this);
        }
    }

    /// <summary>Takes the fence when nothing is in flight and <paramref name="isBusy"/> reports idle.</summary>
    public AcquireResult TryAcquire(Func<bool> isBusy, out Hold? hold) {
        lock (_gate) {
            hold = null;
            if (FencedLocked()) return AcquireResult.Fenced;
            if (_inFlight > 0 || isBusy()) return AcquireResult.Busy;
            hold = _held = new Hold(this);
            return AcquireResult.Acquired;
        }
    }

    bool FencedLocked() {
        // The lease bounds a commit whether or not its connection is still open.
        if (_committedAt is { } committedAt) {
            if (_time.GetUtcNow() < committedAt + CommitLease) return true;

            _committedAt = null;
            _held = null;
            DeleteMarker();
            LogLeaseExpired();
            return false;
        }
        return _held is not null;
    }

    void Leave() {
        lock (_gate) _inFlight--;
    }

    bool CommitLocked(Hold hold) {
        if (!ReferenceEquals(_held, hold)) return false;
        // A repeated commit keeps the first timestamp, so it cannot extend the lease.
        if (_committedAt is not null) return true;
        var now = _time.GetUtcNow();
        if (!WriteMarker(now)) return false;
        _committedAt = now;
        return true;
    }

    bool AbortLocked(Hold hold) {
        if (!ReferenceEquals(_held, hold)) return false;
        // A marker that cannot be deleted would fence the next process, so the commit stands with it.
        if (_committedAt is not null && !DeleteMarker()) return false;
        _held = null;
        _committedAt = null;
        return true;
    }

    void CloseLocked(Hold hold) {
        // A committed fence stays: the closing CLI may be mid-bootout.
        if (ReferenceEquals(_held, hold)) _held = null;
    }

    DateTimeOffset? ReadMarker() {
        if (!File.Exists(_markerPath)) return null;
        DateTimeOffset committedAt;
        try {
            var marker = JsonSerializer.Deserialize(File.ReadAllText(_markerPath), AdmissionFenceJsonContext.Default.RetiringMarker);
            committedAt = marker?.CommittedAt ?? throw new JsonException("marker has no committed_at");
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            // Fail closed: an unreadable marker may be a real commit.
            committedAt = TryModifiedAt() ?? _time.GetUtcNow();
            LogUnreadableMarker(ex.Message);
        }

        if (_time.GetUtcNow() < committedAt + CommitLease) {
            LogStartingFenced(committedAt);
            return committedAt;
        }
        DeleteMarker();
        return null;
    }

    DateTimeOffset? TryModifiedAt() {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(_markerPath), TimeSpan.Zero); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    bool WriteMarker(DateTimeOffset committedAt) {
        try {
            var dir = Path.GetDirectoryName(_markerPath)!;
            Directory.CreateDirectory(dir);
            var tmp = _markerPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            var json = JsonSerializer.Serialize(new RetiringMarker(_instanceId, committedAt), AdmissionFenceJsonContext.Default.RetiringMarker);

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var fs = new FileStream(tmp, options)) {
                fs.Write(Encoding.UTF8.GetBytes(json));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _markerPath, overwrite: true);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogMarkerWriteFailed(ex.Message);
            return false;
        }
    }

    bool DeleteMarker() {
        try {
            File.Delete(_markerPath);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogMarkerDeleteFailed(ex.Message);
            return false;
        }
    }

    sealed class Admission(AdmissionFence fence) : IDisposable {
        int _disposed;

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) fence.Leave();
        }
    }

    /// <summary>One rename's claim on the fence, tied to its control connection.</summary>
    public sealed class Hold(AdmissionFence fence) {
        /// <summary>Persists the commit; true only once the marker is on disk.</summary>
        public bool Commit() {
            lock (fence._gate) return fence.CommitLocked(this);
        }

        /// <summary>Undoes this hold, committed or not; false when it is no longer this hold's, or a
        /// committed marker could not be deleted.</summary>
        public bool Abort() {
            lock (fence._gate) return fence.AbortLocked(this);
        }

        /// <summary>The connection ended: a held fence is released, a committed one stays.</summary>
        public void Close() {
            lock (fence._gate) fence.CloseLocked(this);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retiring marker unreadable ({Reason}); starting fenced")]
    partial void LogUnreadableMarker(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retiring marker committed at {CommittedAt:O} is live; refusing new work until its lease ends")]
    partial void LogStartingFenced(DateTimeOffset committedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rename fence lease ended; admitting new work again")]
    partial void LogLeaseExpired();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the retiring marker: {Reason}")]
    partial void LogMarkerWriteFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the retiring marker: {Reason}")]
    partial void LogMarkerDeleteFailed(string reason);
}
