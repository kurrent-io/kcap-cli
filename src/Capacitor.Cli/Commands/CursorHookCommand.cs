using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Cursor;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Cursor hooks. Cursor invokes the same
/// command for every hook event with <c>hook_event_name</c> in the JSON
/// payload, so we collapse the 8 event handlers behind one CLI entry
/// point. Mirrors <see cref="CodexHookCommand"/>'s shape but adds a
/// shared 2-second wall-clock budget, a per-session canonical-event
/// spool, and a watermark-driven transcript-line backfill.
/// </summary>
public static class CursorHookCommand {
    static readonly TimeSpan DispatcherBudget = TimeSpan.FromSeconds(2);
    static readonly TimeSpan HookPostTimeout  = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Production entry point. Two layers of budget enforcement:
    ///   1. A linked <see cref="CancellationTokenSource"/> threaded into every
    ///      call that honours it (auth-config discovery, HTTP POSTs in
    ///      <see cref="HandleCore"/>, transcript backfill).
    ///   2. A hard <see cref="Task.WhenAny"/> ceiling around the whole pipeline
    ///      because some paths inside <c>TokenStore</c> (refresh against
    ///      <c>/auth/refresh</c> or WorkOS's token endpoint) don't honour a
    ///      <see cref="CancellationToken"/> and would otherwise sit on the
    ///      default 100 s <see cref="HttpClient"/> timeout. If the hard
    ///      ceiling fires we abandon the inner task — the process exits 0
    ///      and the OS reclaims the socket. Cursor's agent loop is
    ///      unblocked even when the auth path hangs.
    /// </summary>
    public static Task<int> Handle(string baseUrl, TextReader stdin) =>
        WithHardCap(HandleInternal(baseUrl, stdin), DispatcherBudget);

    /// <summary>
    /// Test seam for the hard-cap race. Returns 0 if the budget fires
    /// before <paramref name="inner"/> completes; otherwise returns
    /// <paramref name="inner"/>'s result.
    /// </summary>
    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        if (winner != inner) return 0;
        return await inner;
    }

    static async Task<int> HandleInternal(string baseUrl, TextReader stdin) {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(DispatcherBudget);
        HttpClient? client = null;
        try {
            client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, cts.Token);
            var spool = new CursorHookSpool(CursorPaths.SpoolDir());
            spool.ReapOlderThan(TimeSpan.FromDays(30));
            var remaining = DispatcherBudget - sw.Elapsed;
            if (remaining <= TimeSpan.Zero) return 0;
            return await HandleCore(client, baseUrl, stdin, spool, remaining);
        } catch {
            // Fail-open contract: never crash Cursor. Covers auth timeout,
            // unreachable server, malformed config, etc.
            return 0;
        } finally {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Test-friendly core. Caller owns the <see cref="HttpClient"/> and
    /// <see cref="CursorHookSpool"/>.
    /// </summary>
    public static async Task<int> HandleCore(
            HttpClient      client,
            string          baseUrl,
            TextReader      stdin,
            CursorHookSpool spool,
            TimeSpan        budgetTotal
        ) {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(budgetTotal);
        var ct = cts.Token;
        bool BudgetExpired() => sw.Elapsed >= budgetTotal;

        try {
            var body = await stdin.ReadToEndAsync(ct);
            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch { return 0; }
            if (node is null) return 0;

            var eventName = TryGetString(node, "hook_event_name");
            if (string.IsNullOrWhiteSpace(eventName)) return 0;
            if (!CursorHookEventMap.TryResolve(eventName, out var mapping)) return 0;

            NormalizeGuidField(node, "session_id");
            node["home_dir"] = PathHelpers.HomeDirectory;
            var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
            if (agentHostId is not null) node["agent_host_id"] = agentHostId;

            if (eventName == "afterAgentThought") {
                var sid = TryGetString(node, "session_id") ?? "";
                var gen = TryGetString(node, "generation_id") ?? "";
                var txt = TryGetString(node, "text") ?? "";
                node["canonical_event_id"] = StableThoughtId(sid, gen, txt);
            }

            var sessionId = TryGetString(node, "session_id");

            if (sessionId is not null && DisabledSessions.IsDisabled(sessionId)) return 0;

            var normalized = node.ToJsonString();

            if (sessionId is not null) {
                await foreach (var entry in spool.DrainAsync(sessionId, ct)) {
                    if (BudgetExpired()) break;
                    if (!CursorHookEventMap.TryResolve(entry.EventName, out var entryMapping)) {
                        await entry.MarkDeliveredAsync();
                        continue;
                    }
                    var ok = await TryPostHookAsync(client, baseUrl, entryMapping.RouteSegment, entry.Body, ct);
                    if (!ok) break;
                    await entry.MarkDeliveredAsync();
                }
            }

            if (BudgetExpired()) {
                // Drain consumed the budget; preserve the fresh event so the
                // next invocation can still deliver it. Without this the new
                // canonical event would be lost when the spool replay backlog
                // is large or the server is slow.
                if (mapping.SpoolOnFailure && sessionId is not null) {
                    spool.Append(sessionId, eventName, normalized);
                }
                return 0;
            }

            var transcriptPath = TryGetString(node, "transcript_path");

            // For sessionEnd the server's HandleSessionEnd clears the per-session
            // CursorAttachmentsFifo before transcript_line normalization could
            // consume any still-queued beforeSubmitPrompt attachments. Drain the
            // transcript BEFORE posting the terminal hook so the FIFO survives
            // long enough for the final user line to attach. For every other
            // event we keep post-then-backfill so lifecycle metadata reaches the
            // server before any new transcript context.
            var drainBeforePost = eventName == "sessionEnd"
                               && sessionId is not null
                               && !string.IsNullOrEmpty(transcriptPath);

            if (drainBeforePost && !BudgetExpired()) {
                await CursorTranscriptBackfill.RunAsync(
                    client, baseUrl, sessionId!, transcriptPath,
                    budget: BudgetExpired, ct);
            }

            if (BudgetExpired()) {
                // Drain consumed the budget; preserve the unposted sessionEnd
                // so the next invocation (if any) can still deliver it.
                if (mapping.SpoolOnFailure && sessionId is not null) {
                    spool.Append(sessionId, eventName, normalized);
                }
                return 0;
            }

            var posted = await TryPostHookAsync(client, baseUrl, mapping.RouteSegment, normalized, ct);
            switch (posted) {
                case false when mapping.SpoolOnFailure && sessionId is not null:
                    spool.Append(sessionId, eventName, normalized); break;
                case true when eventName == "sessionEnd" && sessionId is not null:
                    spool.DeleteSession(sessionId); break;
            }

            if (!drainBeforePost && !BudgetExpired() && sessionId is not null && !string.IsNullOrEmpty(transcriptPath)) {
                await CursorTranscriptBackfill.RunAsync(
                    client, baseUrl, sessionId, transcriptPath,
                    budget: BudgetExpired, ct);
            }

            return 0;
        } catch {
            // Fail-open per design: any exception (budget cancellation,
            // transcript-file IO race, JSON quirk we missed) must never
            // crash Cursor's agent loop.
            return 0;
        }
    }

    static async Task<bool> TryPostHookAsync(
            HttpClient        client,
            string            baseUrl,
            string            routeSegment,
            string            bodyJson,
            CancellationToken ct
        ) {
        try {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var resp = await client.PostOnceAsync(
                $"{baseUrl}/hooks/{routeSegment}", content, HookPostTimeout, ct);
            return resp.IsSuccessStatusCode;
        } catch { return false; }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = TryGetString(node, fieldName);
        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    static string StableThoughtId(string sessionId, string generationId, string text) {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var hash16 = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return $"{sessionId}:reasoning:{generationId}:{hash16}";
    }

    /// <summary>
    /// Safely extracts a string from <paramref name="node"/>[<paramref name="field"/>].
    /// Returns null (instead of throwing) when the field is absent, null, or not a string.
    /// </summary>
    static string? TryGetString(JsonNode? node, string field) {
        if (node?[field] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }
}
