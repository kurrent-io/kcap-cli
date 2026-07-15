using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Global, session-agnostic drain pass run at the start of every kcap invocation (and periodically by
/// the daemon). Enforces cross-spool ordering per session: spooled session-start → transcript tail
/// (+ needs-import marker) → spooled session-end. Idempotent under repeated/partial passes via
/// deterministic server ids. Stdout-contract hooks (Codex) run this in the BACKGROUND / AFTER stdout.
/// </summary>
public static class LifecycleSpoolDrain {
    // Injectable core (test seam). Ordering is enforced by draining the lifecycle spool's START routes
    // first, then the transcript spool, then the lifecycle END routes — per session.
    public static async Task RunAsync(
            HookSpool lifecycle, TranscriptSpool transcript, string? currentSessionId,
            Func<string, string, Task<DrainOutcome>> lifecyclePoster,
            Func<string, Task<DrainOutcome>>         transcriptPoster,
            TimeSpan budget, CancellationToken ct) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool Expired() => sw.Elapsed >= budget || ct.IsCancellationRequested;

        foreach (var sid in OrderedSessions(lifecycle, transcript, currentSessionId)) {
            if (Expired()) return;
            // Phase 1: session-start (and any non-terminal lifecycle) BEFORE transcript tail.
            await lifecycle.DrainRoutesAsync(sid, isTerminal: false, lifecyclePoster, Expired, ct);
            if (Expired()) return;
            // Phase 2: transcript tail. If it can't fully drain, DO NOT drain session-end (ordering).
            await transcript.DrainAsync(sid, transcriptPoster, Expired, ct);
            if (transcript.HasBacklog(sid)) continue;
            if (transcript.NeedsImport(sid)) await DeliverNeedsImportAsync(sid, lifecyclePoster);
            if (Expired()) return;
            // Phase 3: session-end LAST.
            await lifecycle.DrainRoutesAsync(sid, isTerminal: true, lifecyclePoster, Expired, ct);
        }
    }

    static async Task DeliverNeedsImportAsync(string sid, Func<string, string, Task<DrainOutcome>> poster) {
        var body = new System.Text.Json.Nodes.JsonObject { ["session_id"] = sid, ["needs_import"] = true }.ToJsonString();
        await poster("session-needs-import", body); // route resolved server-side; best-effort
    }

    static IEnumerable<string> OrderedSessions(HookSpool life, TranscriptSpool tx, string? current) {
        var ids = new List<string>();
        void Add(string? s) { if (s is not null && !ids.Contains(s)) ids.Add(s); }
        Add(current);
        foreach (var s in life.SessionIdsWithBacklog()) Add(s);
        foreach (var s in tx.SessionIdsWithBacklog()) Add(s);
        return ids;
    }

    // Production wrapper: real HTTP posters that map status → DrainOutcome (mirrors ClaudeHookCommand's
    // ClaudePoster / CursorHookCommand's PostOnce-based poster).
    public static Task RunAsync(HttpClient client, string baseUrl, HookSpool lifecycle, TranscriptSpool transcript,
                                string? currentSessionId, TimeSpan budget, CancellationToken ct)
        => RunAsync(lifecycle, transcript, currentSessionId,
            lifecyclePoster: (route, body) => PostOnce(client, baseUrl, route, body, ct),
            transcriptPoster: body => PostTranscript(client, baseUrl, body, ct),
            budget, ct);

    static async Task<DrainOutcome> PostOnce(HttpClient client, string baseUrl, string route, string body, CancellationToken ct) {
        try {
            using var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostOnceAsync($"{baseUrl}/hooks/{route}", content, TimeSpan.FromSeconds(3), ct);
            if (resp.IsSuccessStatusCode) return DrainOutcome.Delivered;
            var code = (int)resp.StatusCode;
            return code is >= 500 or 408 or 429 ? DrainOutcome.TransientStop : DrainOutcome.Drop;
        } catch { return DrainOutcome.TransientStop; }
    }

    static Task<DrainOutcome> PostTranscript(HttpClient client, string baseUrl, string body, CancellationToken ct)
        => PostOnce(client, baseUrl, "transcript", body, ct);
}
