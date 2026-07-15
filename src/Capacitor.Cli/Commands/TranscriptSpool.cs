using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Dedicated on-disk spool for the UNDELIVERED TRANSCRIPT TAIL captured at shutdown during an
/// outage. Unlike <see cref="HookSpool"/> (1 MB drop-oldest — acceptable for small lifecycle POSTs)
/// this is bounded with NO SILENT DROP: on cap exhaustion it stops appending and writes a
/// <c>needs-import</c> marker so the session is surfaced as requiring `kcap import` rather than
/// truncated. Per-session JSONL of transcript batch JSON, one per line, arrival order.
/// </summary>
public sealed partial class TranscriptSpool(string spoolDir, long capBytes = TranscriptSpool.DefaultCapBytes) {
    public const long DefaultCapBytes = 8_388_608; // 8 MB per session
    public enum AppendResult { Appended, MarkedNeedsImport }

    static readonly Regex SafeSessionId = SafeSessionIdRegex();
    static int seqCounter;

    /// <summary>The directory where spool files are stored.</summary>
    internal string Dir => spoolDir;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{sessionId}.transcript.jsonl") : null;

    string? MarkerPathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{sessionId}.needs-import") : null;

    public AppendResult Append(string sessionId, string batchJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return AppendResult.Appended; // malformed id — nothing we can key on
        try {
            Directory.CreateDirectory(spoolDir);
            var line = batchJson.Replace("\n", "").Replace("\r", "");
            var incoming = Encoding.UTF8.GetByteCount(line) + 1;
            var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (existing + incoming > capBytes) {
                // NO drop-oldest: preserve what we have and surface the gap honestly.
                MarkNeedsImport(sessionId, $"transcript tail exceeded {capBytes}-byte spool cap");
                return AppendResult.MarkedNeedsImport;
            }
            File.AppendAllText(path, $"{line}\n");
            return AppendResult.Appended;
        } catch { return AppendResult.Appended; } // best effort — never throw on the shutdown path
    }

    public void MarkNeedsImport(string sessionId, string reason) {
        var p = MarkerPathFor(sessionId);
        if (p is null) return;
        try { Directory.CreateDirectory(spoolDir); File.WriteAllText(p, $"{DateTimeOffset.UtcNow:O} {reason}\n"); } catch { }
    }

    public bool NeedsImport(string sessionId) {
        var p = MarkerPathFor(sessionId);
        return p is not null && File.Exists(p);
    }

    /// <summary>True if this session still has undelivered spool entries (live .jsonl or .draining temp).</summary>
    public bool HasBacklog(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && Directory.Exists(spoolDir)
        && (File.Exists(Path.Combine(spoolDir, $"{sessionId}.transcript.jsonl"))
            || Directory.EnumerateFiles(spoolDir, $"{sessionId}.*.transcript.draining").Any());

    public async Task DrainAsync(string sessionId, Func<string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        var live = LivePathFor(sessionId);
        if (live is null || !Directory.Exists(spoolDir)) return;

        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sessionId}.*.transcript.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return;
            if (await DrainFileAsync(temp, poster, expired, ct)) return; // transient → stop, keep remainder
        }

        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return;

        var rotated = Path.Combine(spoolDir, $"{sessionId}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.transcript.draining");
        try { File.Move(live, rotated); }
        catch { return; } // lost the atomic-rename race (or vanished) — the winner handles it
        await DrainFileAsync(rotated, poster, expired, ct);
    }

    // Drain a private temp. Delivered advances; TransientStop or budget stops and keeps the remainder.
    static async Task<bool> DrainFileAsync(
            string path, Func<string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return false; }

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            DrainOutcome outcome;
            try { outcome = await poster(lines[i]); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
        }

        if (i >= lines.Length) {
            try { File.Delete(path); } catch { }
            return false;
        }
        try { await File.WriteAllLinesAsync(path, lines.Skip(i), ct); } catch { }
        return true;
    }

    public void ReapOlderThan(TimeSpan age) {
        try {
            if (!Directory.Exists(spoolDir)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var file in Directory.EnumerateFiles(spoolDir)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
