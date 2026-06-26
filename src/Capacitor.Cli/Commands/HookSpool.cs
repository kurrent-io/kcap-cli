using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>Result of a single spooled-entry replay attempt.</summary>
public enum DrainOutcome {
    Delivered,    // POST succeeded — advance past the entry
    Drop,         // permanent failure (4xx except 408/429) — discard, do not retry
    TransientStop // server down/timeout/budget — stop draining, keep the remainder
}

/// <summary>
/// Vendor-neutral on-disk spool for lifecycle hook POSTs whose delivery failed.
/// Per-session JSONL (<c>{spoolDir}/&lt;dashless-sid&gt;.jsonl</c>), one
/// <c>{"route","body"}</c> object per line in arrival order. Drains are
/// rotate-on-drain: the live file is atomically renamed to a private
/// <c>.draining</c> temp before reading, so concurrent appends never collide
/// with an in-flight drain.
/// </summary>
public sealed partial class HookSpool(string spoolDir, int capBytes = HookSpool.DefaultCapBytes) {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    static readonly Regex SafeSessionId = SafeSessionIdRegex();
    static          int   seqCounter;

    /// <summary>The directory where spool files are stored.</summary>
    internal string Dir => spoolDir;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{sessionId}.jsonl") : null;

    public void Append(string sessionId, string route, string rawPayloadJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return;
        try {
            Directory.CreateDirectory(spoolDir);
            var line = new JsonObject { ["route"] = route, ["body"] = rawPayloadJson }.ToJsonString();
            EnsureUnderCap(path, Encoding.UTF8.GetByteCount(line) + 1);
            File.AppendAllText(path, $"{line}\n");
        } catch { /* best effort */ }
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length + incomingBytes <= capBytes) return;
            // Count UTF-8 BYTES (not chars) so the cap holds for non-ASCII payloads — the file is
            // byte-measured (FileInfo.Length), so a char-based count would under-count and let the
            // file grow past capBytes.
            var lines = File.ReadAllLines(path).ToList();
            while (lines.Count > 0 && lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1) + incomingBytes > capBytes)
                lines.RemoveAt(0);
            File.WriteAllText(path, lines.Count > 0 ? string.Join('\n', lines) + "\n" : "");
        } catch { }
    }

    public async Task DrainAllAsync(
            string?                                   currentSessionId,
            Func<string, string, Task<DrainOutcome>>  poster,
            TimeSpan                                  budget,
            CancellationToken                         ct) {
        if (!Directory.Exists(spoolDir)) return;
        var sw = Stopwatch.StartNew();
        bool Expired() => sw.Elapsed >= budget;

        foreach (var sid in OrderedSessionIds(currentSessionId)) {
            if (Expired() || ct.IsCancellationRequested) return;
            if (await DrainSessionAsync(sid, poster, Expired, ct)) return; // transient → stop the pass
        }
    }

    // Current session first (if it has anything), then every other session once.
    IEnumerable<string> OrderedSessionIds(string? currentSessionId) {
        var ids = new List<string>();
        if (currentSessionId is not null && SafeSessionId.IsMatch(currentSessionId) && HasAny(currentSessionId))
            ids.Add(currentSessionId);
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var sid = SessionIdOf(f);
            if (sid is not null && !ids.Contains(sid)) ids.Add(sid);
        }
        return ids;
    }

    bool HasAny(string sid) =>
        File.Exists(Path.Combine(spoolDir, $"{sid}.jsonl"))
     || Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").Any();

    static string? SessionIdOf(string filePath) {
        var name = Path.GetFileName(filePath);
        var dot  = name.IndexOf('.');
        if (dot <= 0) return null;
        var sid = name[..dot];
        return SafeSessionId.IsMatch(sid) ? sid : null;
    }

    // Recovered temps (oldest first) then the rotated live file. Returns true => stop the whole pass.
    async Task<bool> DrainSessionAsync(
            string sid, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return false;
            if (await DrainFileAsync(temp, poster, expired, ct)) return true;
        }

        var live = Path.Combine(spoolDir, $"{sid}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return false;

        var rotated = Path.Combine(spoolDir, $"{sid}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.draining");
        try { File.Move(live, rotated); }
        catch { return false; } // lost the atomic-rename race (or vanished) — the winner handles it
        return await DrainFileAsync(rotated, poster, expired, ct);
    }

    // Drain a private temp. Delivered/Drop advance; TransientStop or budget stops and keeps the remainder.
    static async Task<bool> DrainFileAsync(
            string path, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return false; }

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
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

    /// <summary>True if this session still has undelivered spool entries (live .jsonl or .draining temp).</summary>
    public bool HasBacklog(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && Directory.Exists(spoolDir)
        && (File.Exists(Path.Combine(spoolDir, $"{sessionId}.jsonl"))
            || Directory.EnumerateFiles(spoolDir, $"{sessionId}.*.draining").Any());

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
