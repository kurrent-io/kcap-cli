using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>get_session_evals</c>: the eval state of a handful of sessions, read per session from
/// <c>/api/sessions/{id}/eval-progress</c>. That route is gated by session visibility alone, so this
/// works on every plan — unlike <c>/api/analytics</c>, which is the paid Insights feature.
/// </summary>
static partial class SessionEvalsTool {
    internal const string Name = "get_session_evals";

    internal const int MaxSessions = 25;

    const int MaxConcurrency = 4;
    const int MaxSummaryChars = 600;

    // The stdio loop is serial, so the whole fan-out shares one deadline.
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SessionIdPattern();

    internal static McpTool Definition => new(
        Name,
        "Get the eval state of up to 25 sessions in one call — the way to follow evals on sessions you already know the ids of, such as the ones a `kcap setup` import created. " +
        "Works on every plan. Each entry carries session_id and state: not_found (not ingested yet, or not visible to you), not_evaluated (ingested, no eval run yet), " +
        "queued (waiting for a worker or a retry: queue_position, next_attempt_at), running (questions_done/total_questions), completed, failed (failure_reason), or error (this lookup failed; http_status or message). " +
        "A completed entry carries eval_run_id, evaluated_at, overall_score (out of 5), judge_model, summary, categories [{name, score}] and weakest_questions, the two lowest-scored assessed questions [{question_id, category, score}]. " +
        "A session being re-evaluated reads running even when an earlier eval exists.",
        new(
            "object",
            new() {
                ["session_ids"] = new("array", "1 to 25 session ids, each matching ^[A-Za-z0-9_-]{1,128}$. Duplicates are answered once.", new("string", "A session id."))
            },
            ["session_ids"]
        ),
        McpToolAnnotations.Read
    );

    internal static IReadOnlyList<string> ParseSessionIds(JsonObject? args) {
        if (args?["session_ids"] is not JsonArray array)
            throw new ArgumentException("Missing required argument: session_ids (an array of session ids).");

        var ids = new List<string>(array.Count);

        foreach (var node in array) {
            if (node is not JsonValue value || !value.TryGetValue(out string? id) || !SessionIdPattern().IsMatch(id))
                throw new ArgumentException("Every session_ids entry must be a string matching ^[A-Za-z0-9_-]{1,128}$.");

            if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }

        return ids.Count switch {
            0             => throw new ArgumentException("session_ids must name at least one session."),
            > MaxSessions => throw new ArgumentException($"session_ids takes at most {MaxSessions} sessions per call."),
            _             => ids
        };
    }

    internal static string BuildEvalProgressUrl(string baseUrl, string sessionId) =>
        $"{baseUrl}/api/sessions/{Uri.EscapeDataString(sessionId)}/eval-progress";

    /// <summary>The per-session entries in request order, or null when the server rejected the
    /// credential: that is the caller's to report once, not 25 times.</summary>
    internal static async Task<JsonObject?> FetchAsync(
            HttpClient client, string baseUrl, IReadOnlyList<string> ids, TimeProvider time) {
        var entries      = new JsonObject?[ids.Count];
        var unauthorized = false;

        using var cts = new CancellationTokenSource(Deadline, time);

        try {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, ids.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cts.Token },
                async (i, ct) => {
                    try {
                        using var response = await client.GetAsync(BuildEvalProgressUrl(baseUrl, ids[i]), ct);

                        if (response.StatusCode == HttpStatusCode.Unauthorized) {
                            unauthorized = true;
                            return;
                        }

                        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct) : null;
                        entries[i] = Project(ids[i], response.StatusCode, body);
                    } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                        entries[i] = Error(ids[i], ex is OperationCanceledException ? "timed out" : ex.Message);
                    }
                });
        } catch (OperationCanceledException) {
            // Entries the deadline never reached stay null and are reported below.
        }

        if (unauthorized) return null;

        var sessions = new JsonArray();
        for (var i = 0; i < ids.Count; i++) sessions.Add((JsonNode)(entries[i] ?? Error(ids[i], "timed out")));

        return new JsonObject { ["sessions"] = sessions };
    }

    internal static JsonObject Project(string sessionId, HttpStatusCode status, string? body) {
        switch (status) {
            case HttpStatusCode.NotFound:  return State(sessionId, "not_found");
            case HttpStatusCode.NoContent: return State(sessionId, "not_evaluated");
            case not HttpStatusCode.OK:    return HttpError(sessionId, status);
        }

        JsonElement root;

        try {
            using var doc = JsonDocument.Parse(body ?? "");
            root = doc.RootElement.Clone();
        } catch (JsonException) {
            return Error(sessionId, "unreadable eval-progress response");
        }

        if (root.Obj("completed_result") is { } result && !IsFailureOnly(result)) return Completed(sessionId, root, result);

        if (root.Bool("is_terminal") == true || root.Obj("completed_result") is not null || root.Obj("failed_result") is not null) {
            var failed = State(sessionId, "failed");
            failed["eval_run_id"]    = root.Str("eval_run_id");
            failed["failure_reason"] = root.Str("failure_reason") ?? "every question failed to judge";
            return failed;
        }

        // A queue position or a retry time means no worker holds the run yet.
        if (root.Num("queue_position") is not null || root.Str("next_attempt_at") is not null) {
            var queued = State(sessionId, "queued");
            queued["eval_run_id"]     = root.Str("eval_run_id");
            queued["queue_position"]  = root.Num("queue_position");
            queued["next_attempt_at"] = root.Str("next_attempt_at");
            return queued;
        }

        var running = State(sessionId, "running");
        running["eval_run_id"]     = root.Str("eval_run_id");
        running["questions_done"]  = Questions(root).Count(q => q.Str("status") is "completed" or "failed");
        running["total_questions"] = root.Num("total_questions");
        return running;
    }

    // A run that judged nothing and recorded only coded failures is a failed run, whichever field carries it.
    static bool IsFailureOnly(JsonElement result) =>
        !Items(result.Arr("categories")).Any() && Items(result.Arr("failed_questions")).Any();

    static JsonObject Completed(string sessionId, JsonElement root, JsonElement result) {
        var entry = State(sessionId, "completed");
        entry["eval_run_id"]   = result.Str("eval_run_id");
        entry["evaluated_at"]  = result.Str("evaluated_at");
        entry["overall_score"] = result.Num("overall_score");
        entry["judge_model"]   = result.Str("judge_model");
        entry["summary"]       = Truncate(result.Str("summary"));

        var categories = new JsonArray();
        var verdicts   = new List<(string Id, string? Category, long Score)>();

        foreach (var category in Items(result.Arr("categories"))) {
            categories.Add((JsonNode)new JsonObject { ["name"] = category.Str("name"), ["score"] = category.Num("score") });

            foreach (var q in Items(category.Arr("questions")))
                if (q.Str("question_id") is { } id && q.Num("score") is { } score)
                    verdicts.Add((id, q.Str("category") ?? category.Str("name"), score));
        }

        entry["categories"] = categories;

        // The live question list carries each question's outcome, so not-applicable and
        // insufficient-evidence answers stay out of "weakest"; the category verdicts are the fallback
        // for a persisted run that kept no question list.
        var assessed = Questions(root)
            .Where(q => q.Str("outcome") is null or "assessed")
            .Select(q => (Id: q.Str("question_id"), Category: q.Str("category"), Score: q.Num("score")))
            .Where(q => q.Id is not null && q.Score is not null)
            .Select(q => (q.Id!, q.Category, q.Score!.Value))
            .ToList();

        var weakest = new JsonArray();
        foreach (var (id, category, score) in (assessed.Count > 0 ? assessed : verdicts)
                     .OrderBy(q => q.Item3).ThenBy(q => q.Item1, StringComparer.Ordinal).Take(2))
            weakest.Add((JsonNode)new JsonObject { ["question_id"] = id, ["category"] = category, ["score"] = score });

        entry["weakest_questions"] = weakest;
        return entry;
    }

    static IEnumerable<JsonElement> Questions(JsonElement root) => Items(root.Arr("questions"));

    static IEnumerable<JsonElement> Items(JsonElement? array) =>
        array is { } a ? a.EnumerateArray().Where(e => e.IsObject) : [];

    static string? Truncate(string? text) =>
        text is { Length: > MaxSummaryChars } ? text[..MaxSummaryChars] + "…" : text;

    static JsonObject State(string sessionId, string state) => new() { ["session_id"] = sessionId, ["state"] = state };

    static JsonObject HttpError(string sessionId, HttpStatusCode status) {
        var entry = State(sessionId, "error");
        entry["http_status"] = (int)status;
        return entry;
    }

    static JsonObject Error(string sessionId, string message) {
        var entry = State(sessionId, "error");
        entry["message"] = message;
        return entry;
    }
}
