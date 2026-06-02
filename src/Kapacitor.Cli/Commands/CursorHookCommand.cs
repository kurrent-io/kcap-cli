using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

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
    /// Production entry point. Resolves the home-dir spool and
    /// authenticated <see cref="HttpClient"/>, then delegates to
    /// <see cref="HandleCore"/>.
    /// </summary>
    public static async Task<int> Handle(string baseUrl, TextReader stdin) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var spool = new CursorHookSpool(CursorPaths.SpoolDir());
        spool.ReapOlderThan(TimeSpan.FromDays(30));
        return await HandleCore(client, baseUrl, stdin, spool, DispatcherBudget);
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
            var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");
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
                    if (BudgetExpired()) return 0;
                    if (!CursorHookEventMap.TryResolve(entry.EventName, out var entryMapping)) {
                        await entry.MarkDeliveredAsync();
                        continue;
                    }
                    var ok = await TryPostHookAsync(client, baseUrl, entryMapping.RouteSegment, entry.Body, ct);
                    if (!ok) break;
                    await entry.MarkDeliveredAsync();
                }
            }

            if (BudgetExpired()) return 0;

            var posted = await TryPostHookAsync(client, baseUrl, mapping.RouteSegment, normalized, ct);
            if (!posted && mapping.SpoolOnFailure && sessionId is not null) {
                spool.Append(sessionId, eventName, normalized);
            }

            if (posted && eventName == "sessionEnd" && sessionId is not null) {
                spool.DeleteSession(sessionId);
            }

            if (!BudgetExpired() && sessionId is not null) {
                var transcriptPath = TryGetString(node, "transcript_path");
                if (!string.IsNullOrEmpty(transcriptPath)) {
                    await CursorTranscriptBackfill.RunAsync(
                        client, baseUrl, sessionId, transcriptPath,
                        budget: BudgetExpired, ct);
                }
            }

            return 0;
        } catch (OperationCanceledException) {
            // Budget expired or external cancellation — fail-open per design.
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
