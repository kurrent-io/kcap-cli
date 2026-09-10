using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Reaps envelope journals kcap owns: older than the retention and with no PID record under the
/// same name. Runs once after the startup orphan reap (which removes a prior epoch's records) and
/// then every 24 hours. Never throws — a sweep fault must not block the daemon's connect.
internal sealed class TranscriptJournalSweep(string stateDir, TimeProvider time, ILogger<TranscriptJournalSweep> logger, JournalPathLocks? locks = null) : BackgroundService {
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public static readonly TimeSpan Interval  = TimeSpan.FromHours(24);

    readonly JournalPathLocks _locks = locks ?? JournalPathLocks.Shared;
    int _running;
    int _started;
    int _finished;

    public int SweepsStarted => Volatile.Read(ref _started);

    /// Rises only after the single-flight flag is released, so a caller that waits on it can drive
    /// the next tick without it being skipped.
    public int SweepsCompleted => Volatile.Read(ref _finished);

    protected override async Task ExecuteAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(Interval, time);
        try {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await RunOnceAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task RunOnceAsync(CancellationToken ct) {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        Interlocked.Increment(ref _started);
        try {
            var dir = Path.Combine(stateDir, "transcripts");
            if (!Directory.Exists(dir)) return;
            var cutoff = time.GetUtcNow() - Retention;
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl")) {
                ct.ThrowIfCancellationRequested();
                await TrySweepAsync(path, cutoff).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            logger.LogWarning(ex, "Transcript journal sweep: enumeration failed");
        } finally {
            Interlocked.Exchange(ref _running, 0);
            Interlocked.Increment(ref _finished);
        }
    }

    async Task TrySweepAsync(string path, DateTimeOffset cutoff) {
        try {
            using var lease = await _locks.AcquireAsync(path, TranscriptJournal.LockBound, CancellationToken.None).ConfigureAwait(false);
            if (lease is null) return;

            // A same-id relaunch reopens the journal under this lock, so everything the delete turns
            // on is read after it is held: a check made while waiting would be about a stale file.
            if (!File.Exists(path)) return;
            if (File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime) return;
            var record = Path.Combine(stateDir, "agents", Path.GetFileNameWithoutExtension(path) + ".json");
            if (File.Exists(record)) return;
            File.Delete(path);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Transcript journal sweep: skipped {Path}", path);
        }
    }
}
