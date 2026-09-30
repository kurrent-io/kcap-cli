using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Continuation;

/// <summary>
/// Attaches the current session to the work items and unfinished plans of the session it continues.
/// A live local process refuses the takeover; a local exit record allows it whatever the server
/// still believes, because a private daemon agent's death never reaches the server. Only with no
/// local evidence does the server's status decide.
/// </summary>
sealed class SessionTakeover(AgentSessions local, TimeProvider time) {
    /// <summary>The server's own threshold for treating an active session as stale.</summary>
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    const string NotInPlanCode = "work_items_not_in_plan";

    public async Task<TakeoverResult> RunAsync(
            HttpClient client, string baseUrl, string previous, string current, bool force, CancellationToken ct = default) {
        if (SessionId.Parse(previous) is not { } previousId)
            return new TakeoverResult.Refused($"'{previous}' is not a session id.");

        if (previousId == SessionId.Parse(current))
            return new TakeoverResult.Refused("A session cannot continue itself: name the session whose work this one takes over.");

        if (WorkContextIds.CanonicalSessionId(previous) is not { } previousWire)
            return new TakeoverResult.Refused($"'{previous}' is not a session id.");

        if (WorkContextIds.CanonicalSessionId(current) is not { } currentWire)
            return new TakeoverResult.Refused($"'{current}' is not a session id.");

        var escaped = Uri.EscapeDataString(previousWire);

        var (summary, summaryError) = await ReadAsync(client, $"{baseUrl}/api/sessions/{escaped}/summary", ct);
        using var summaryResponse = summary;

        if (summary is null)
            return new TakeoverResult.Failed($"Reading session {previous} failed: {summaryError}");

        if (summary.StatusCode == HttpStatusCode.Unauthorized) return new TakeoverResult.Unauthorized();
        if (summary.StatusCode == HttpStatusCode.NotFound)
            return new TakeoverResult.Refused($"Session {previous} was not found or is not visible to you.");
        if (!summary.IsSuccessStatusCode)
            return new TakeoverResult.Failed($"Reading session {previous} failed: HTTP {(int)summary.StatusCode}.");

        local.Reap();

        var (liveness, refusal) = Judge(previous, local.Liveness(previousId), ParseObject(await summary.Content.ReadAsStringAsync(ct)), force);
        if (refusal is not null) return new TakeoverResult.Refused(refusal);

        var itemsRead = ReadAsync(client, $"{baseUrl}/api/work-items/session/{escaped}", ct);
        var plansRead = ReadAsync(client, $"{baseUrl}/api/sessions/{escaped}/plans", ct);
        var (items, itemsError) = await itemsRead;
        var (plans, plansError) = await plansRead;
        using var itemsResponse = items;
        using var plansResponse = plans;

        var writes  = new WriteCount();
        var outcome = new JsonObject {
            ["continued_from"] = previousWire,
            ["liveness"]       = liveness,
            ["work_items"]     = await AttachWorkItemsAsync(client, baseUrl, items, itemsError, currentWire, writes, ct),
        };

        await AdoptPlansAsync(client, baseUrl, plans, plansError, currentWire, outcome, writes, ct);

        return new TakeoverResult.Completed(outcome, writes.Attempted > 0 && writes.Failed == writes.Attempted);
    }

    (string Liveness, string? Refusal) Judge(string previous, SessionLiveness here, JsonObject? summary, bool force) {
        if (force) return ("forced", null);

        switch (here) {
            case SessionLiveness.Running:
                return ("running", $"Session {previous} is still running on this machine. Take it over only if the user confirms, by retrying with force.");
            case SessionLiveness.Exited:
                return ("exited", null);
        }

        if (string.Equals(Str(summary?["status"]), "ended", StringComparison.OrdinalIgnoreCase)) return ("ended", null);

        var lastSeen = Time(summary?["last_event_at"]) ?? Time(summary?["started_at"]);
        if (lastSeen is { } seen && time.GetUtcNow() - seen > StaleAfter) return ("stale", null);

        var when = lastSeen?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown";

        return ("active", $"Session {previous} still looks active (last event {when}) and did not run on this machine. If its agent is gone, ask the user, then retry with force.");
    }

    static async Task<JsonObject> AttachWorkItemsAsync(
            HttpClient client, string baseUrl, HttpResponseMessage? read, string? readError, string current, WriteCount writes, CancellationToken ct) {
        if (read is null)
            return new JsonObject { ["status"] = "failed", ["error"] = readError, ["items"] = new JsonArray() };

        var body = await read.Content.ReadAsStringAsync(ct);

        if (read.StatusCode == HttpStatusCode.Forbidden && Str(ParseObject(body)?["code"]) == NotInPlanCode)
            return new JsonObject { ["status"] = "not_in_plan", ["items"] = new JsonArray() };

        if (!read.IsSuccessStatusCode)
            return new JsonObject { ["status"] = "failed", ["error"] = $"HTTP {(int)read.StatusCode}", ["items"] = new JsonArray() };

        var attached = new JsonArray();

        foreach (var item in ParseArray(body)) {
            if (Str(item?["work_item_id"]) is not { } workItemId) continue;

            var entry = new JsonObject { ["work_item_id"] = workItemId, ["label"] = Str(item?["label"]) ?? workItemId };
            var declare = new JsonObject { ["session_id"] = current, ["work_item_id"] = workItemId };

            await WriteAsync(client, $"{baseUrl}/api/work-items/declare", declare, entry, writes, ct);
            attached.Add((JsonNode?)entry);
        }

        return new JsonObject { ["status"] = "ok", ["items"] = attached };
    }

    static async Task AdoptPlansAsync(
            HttpClient client, string baseUrl, HttpResponseMessage? read, string? readError, string current, JsonObject outcome, WriteCount writes, CancellationToken ct) {
        var adopted = new JsonArray();
        var skipped = new JsonArray();
        outcome["plans"]         = adopted;
        outcome["skipped_plans"] = skipped;

        if (read is null || !read.IsSuccessStatusCode) {
            outcome["plans_error"] = read is null ? readError : $"HTTP {(int)read.StatusCode}";
            return;
        }

        string? currentPlan = null;

        // The previous session's current plan goes last, so it ends up current on this one too.
        var plans = ParseArray(await read.Content.ReadAsStringAsync(ct))
            .OfType<JsonObject>()
            .Where(p => Str(p["plan_id"]) is not null)
            .OrderBy(p => IsTrue(p["is_current"]))
            .ToList();

        foreach (var plan in plans) {
            var planId = Str(plan["plan_id"])!;

            if (DeclaredPlanState.IsFinished(plan)) {
                skipped.Add((JsonNode?)Skip(planId, "finished"));
                continue;
            }

            var open = OpenTasks(plan);
            if (open.Count == 0) {
                skipped.Add((JsonNode?)Skip(planId, "no_open_task"));
                continue;
            }

            if (Adoptable(open) is not { } task) {
                skipped.Add((JsonNode?)Skip(planId, "user_owned"));
                continue;
            }

            var taskId = Str(task["task_id"])!;
            var status = Str(task["status"])!;
            var entry  = new JsonObject {
                ["plan_id"] = planId, ["task_id"] = taskId, ["title"] = Str(task["title"]), ["status"] = status,
            };

            // Status and note both go back unchanged: the server compares the two, so anything less
            // would record a change, and a dropped note would erase it.
            var update = new JsonObject { ["session_id"] = current, ["status"] = status };
            if (task["note"] is JsonValue noteValue && noteValue.TryGetValue(out string? note) && note is not null) update["note"] = note;

            var url = $"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks/{Uri.EscapeDataString(taskId)}";
            if (await WriteAsync(client, url, update, entry, writes, ct)) currentPlan = planId;

            adopted.Add((JsonNode?)entry);
        }

        if (currentPlan is not null) outcome["current_plan_id"] = currentPlan;
    }

    static List<JsonObject> OpenTasks(JsonObject plan) =>
        plan["tasks"] is JsonArray tasks
            ? tasks.OfType<JsonObject>()
                   .Where(t => Str(t["task_id"]) is not null && Str(t["status"]) is "in_progress" or "pending")
                   .OrderBy(t => t["ordinal"] is JsonValue v && v.TryGetValue(out int o) ? o : int.MaxValue)
                   .ToList()
            : [];

    /// <summary>A user-set status outranks an MCP write, so the server would refuse even an unchanged one.</summary>
    static JsonObject? Adoptable(List<JsonObject> open) {
        var mine = open.Where(t => Str(t["source"]) != "user").ToList();

        return mine.FirstOrDefault(t => Str(t["status"]) == "in_progress") ?? mine.FirstOrDefault();
    }

    static async Task<bool> WriteAsync(HttpClient client, string url, JsonObject body, JsonObject entry, WriteCount writes, CancellationToken ct) {
        writes.Attempted++;

        try {
            using var response = await client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

            if (response.IsSuccessStatusCode) {
                entry["attached"] = true;
                return true;
            }

            entry["error"] = $"HTTP {(int)response.StatusCode}";
        } catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) {
            entry["error"] = ex.Message;
        }

        entry["attached"] = false;
        writes.Failed++;

        return false;
    }

    static async Task<(HttpResponseMessage? Response, string? Error)> ReadAsync(HttpClient client, string url, CancellationToken ct) {
        try {
            return (await client.GetAsync(url, ct), null);
        } catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) {
            return (null, ex.Message);
        }
    }

    static JsonObject Skip(string planId, string reason) => new() { ["plan_id"] = planId, ["reason"] = reason };

    static JsonObject? ParseObject(string text) {
        try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
    }

    static JsonArray ParseArray(string text) {
        try { return JsonNode.Parse(text) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) ? s : null;

    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;

    static DateTimeOffset? Time(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    sealed class WriteCount {
        public int Attempted;
        public int Failed;
    }
}
