using System.Security.Cryptography;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One evidence-route run's private working state: an owner-only directory holding each question's run file and
/// ledger, and the retained facts buffered until the run ends cleanly. Disposal deletes the directory; a crash's leftovers
/// are removed by <see cref="SweepStale"/> at the next start.</summary>
public sealed class EvidenceRunContext : IAsyncDisposable {
    public const int    MaxStaleSweep   = 32;
    public const string DirectoryPrefix = "kcap-eval-";
    public static readonly TimeSpan StaleRunDirectoryAge = TimeSpan.FromHours(24);

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    readonly List<(string Category, EvalService.RetainedFact Fact)> _retained = [];
    readonly Lock _gate = new();
    int _deleted;

    EvidenceRunContext(string runDirectory, string evalRunId) {
        RunDirectory = runDirectory;
        EvalRunId    = evalRunId;
    }

    public string RunDirectory { get; }
    public string EvalRunId    { get; }

    public static EvidenceRunContext Create(string evalRunId, string? tempRoot = null, bool? isWindows = null) {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var dir     = Path.Combine(tempRoot ?? Path.GetTempPath(), DirectoryPrefix + RandomNumberGenerator.GetHexString(16, lowercase: true));
        if (windows) {
            // Inherits the per-user temp directory's ACL, as the token store's files do.
            Directory.CreateDirectory(dir);
        } else {
#pragma warning disable CA1416 // guarded by the isWindows flag, which defaults to the real platform
            Directory.CreateDirectory(dir, OwnerOnly);
            File.SetUnixFileMode(dir, OwnerOnly);
#pragma warning restore CA1416
        }
        return new(dir, evalRunId);
    }

    public string RunFilePath(int ordinal)    => Path.Combine(RunDirectory, $"q{ordinal}.run.json");
    public string LedgerFilePath(int ordinal) => Path.Combine(RunDirectory, $"q{ordinal}.ledger.jsonl");

    public void DeleteRunFile(int ordinal) {
        try { File.Delete(RunFilePath(ordinal)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void BufferRetainedFact(string category, EvalService.RetainedFact fact) { lock (_gate) _retained.Add((category, fact)); }

    public int BufferedFactCount { get { lock (_gate) return _retained.Count; } }

    public void DiscardRetainedFacts() { lock (_gate) _retained.Clear(); }

    /// <summary>Posts every buffered fact once and reports each one the server accepted; the buffer is empty afterwards.</summary>
    public async Task<int> DrainRetainedFactsAsync(Func<string, EvalService.RetainedFact, CancellationToken, Task<bool>> post, IEvalObserver observer, CancellationToken ct) {
        List<(string Category, EvalService.RetainedFact Fact)> facts;
        lock (_gate) { facts = [.. _retained]; _retained.Clear(); }

        var posted = 0;
        foreach (var (category, fact) in facts) {
            if (!await post(category, fact, ct)) continue;
            observer.OnFactRetained(category, fact.Fact);
            posted++;
        }
        return posted;
    }

    /// <summary>Removes at most <see cref="MaxStaleSweep"/> owner-only run directories older than
    /// <see cref="StaleRunDirectoryAge"/>; one it cannot remove is logged and skipped.</summary>
    public static int SweepStale(string tempRoot, TimeProvider time, Action<string> log, bool? isWindows = null) {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(tempRoot, DirectoryPrefix + "*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"stale eval run sweep skipped: {e.Message}"); return 0; }

        var cutoff  = (time.GetUtcNow() - StaleRunDirectoryAge).UtcDateTime;
        var removed = 0;
        foreach (var dir in candidates) {
            if (removed >= MaxStaleSweep) break;
            try {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null || info.LastWriteTimeUtc > cutoff) continue;
#pragma warning disable CA1416
                if (!windows && info.UnixFileMode != OwnerOnly) continue;
#pragma warning restore CA1416
                info.Delete(recursive: true);
                removed++;
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                log($"could not remove stale eval run directory {dir}: {e.Message}");
            }
        }
        return removed;
    }

    public ValueTask DisposeAsync() {
        DeleteDirectory();
        return ValueTask.CompletedTask;
    }

    /// <summary>Discards the buffer and deletes the directory; a later call does nothing. An I/O failure propagates to the
    /// caller, which logs it.</summary>
    public void DeleteDirectory() {
        if (Interlocked.Exchange(ref _deleted, 1) == 1) return;
        DiscardRetainedFacts();
        if (Directory.Exists(RunDirectory)) Directory.Delete(RunDirectory, recursive: true);
    }
}
