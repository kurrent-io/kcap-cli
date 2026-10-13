using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Continuation;

/// <summary>A local exit record can authorize takeover even before the server observes the exit.
/// Claim ownership is independently admitted by the server; force never bypasses that fence.</summary>
sealed class SessionTakeover(AgentSessions local, TimeProvider time) {
    /// <summary>The server's own threshold for treating an active session as stale.</summary>
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    const string NotInPlanCode = "work_items_not_in_plan";

    const string MalformedResponse = "malformed response";

    /// <summary>The plans endpoint returns at most this many, the most recently touched, with no paging.</summary>
    public const int PlansReadCap = 20;

    public async Task<TakeoverResult> RunAsync(
            HttpClient client, string baseUrl, string previous, string current, bool force, CancellationToken ct = default) {
        if (WorkContextIds.CanonicalSessionId(previous) is not { } previousWire || SessionId.Parse(previousWire) is not { } previousId)
            return new TakeoverResult.Refused($"'{previous}' is not a session id.");

        if (WorkContextIds.CanonicalSessionId(current) is not { } currentWire)
            return new TakeoverResult.Refused($"'{current}' is not a session id.");

        if (previousWire == currentWire)
            return new TakeoverResult.Refused("A session cannot continue itself: name the session whose work this one takes over.");

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

        if (items?.StatusCode == HttpStatusCode.Unauthorized || plans?.StatusCode == HttpStatusCode.Unauthorized)
            return new TakeoverResult.Unauthorized();

        var writes  = new WriteCount();
        var outcome = new JsonObject {
            ["continued_from"] = previousWire,
            ["liveness"]       = liveness,
            ["work_items"]     = await AttachWorkItemsAsync(client, baseUrl, items, itemsError, currentWire, writes, ct),
        };

        await AdoptPlansAsync(client, baseUrl, plans, plansError, currentWire, outcome, writes, ct);
        outcome["loose_end_claims"] = await AdoptClaimsAsync(client, baseUrl, previousWire, currentWire, writes, ct);

        var readFailed = Str(outcome["work_items"]?["status"]) == "failed" || outcome["plans_error"] is not null;

        if (writes.Unauthorized) outcome["unauthorized"] = true;

        return new TakeoverResult.Completed(outcome, Unsuccessful: writes.Failed > 0 || readFailed, writes.Unauthorized);
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

        if (ParseArray(body) is not { } list)
            return new JsonObject { ["status"] = "failed", ["error"] = MalformedResponse, ["items"] = new JsonArray() };

        var attached = new JsonArray();

        // The last declare becomes primary, so the previous session's primary goes last.
        foreach (var item in list.OrderBy(i => IsTrue(i?["is_primary"])).ToList()) {
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

        if (ParseArray(await read.Content.ReadAsStringAsync(ct)) is not { } list) {
            outcome["plans_error"] = MalformedResponse;
            return;
        }

        if (list.Count >= PlansReadCap) outcome["plans_truncated"] = true;

        string? currentPlan = null;

        // The previous session's current plan goes last, so it ends up current on this one too.
        var plans = list
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
                skipped.Add((JsonNode?)Skip(planId, "not_adoptable"));
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

    static async Task<JsonObject> AdoptClaimsAsync(
            HttpClient client, string baseUrl, string previous, string current, WriteCount writes, CancellationToken ct) {
        var result = (JsonObject)JsonNode.Parse("""{"status":"failed","results":[]}""")!;
        writes.Attempted++;
        try {
            var body = $"{{\"previous_session_id\":\"{JsonEncodedText.Encode(previous)}\",\"session_id\":\"{JsonEncodedText.Encode(current)}\"}}";
            using var response = await client.PostAsync($"{baseUrl}/api/loose-ends/adopt", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var json = ParseObject(await response.Content.ReadAsStringAsync(ct));
            var code = Str(json?["code"]);
            if (!response.IsSuccessStatusCode) {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && code is null) {
                    result["status"] = JsonString("unsupported");
                    writes.Failed++;
                    return result;
                }
                if (code == "next_work_unavailable") result["status"] = JsonString("unavailable");
                result["error"] = JsonString($"HTTP {(int)response.StatusCode}" + (code is not null && NextWorkEmitter.IsCode(code) ? $" — {code}" : ""));
                if (response.StatusCode == HttpStatusCode.Unauthorized) writes.Unauthorized = true;
            } else if (json?["results"] is JsonArray results) {
                result["results"] = results.DeepClone();
                var partial = results.Any(entry => entry is not JsonObject item ||
                    Str(item["outcome"]) is not ("acquired" or "already_owned" or "transferred" or "recorded_catching_up") ||
                    Str(item["attempted_claim_id"]) is null ||
                    item["claim"] is not JsonObject claim || Str(claim["claim_id"]) is null ||
                    WorkContextIds.CanonicalSessionId(Str(claim["session_id"])) != current);
                result["status"] = JsonString(partial ? "partial" : "ok");
                if (partial) writes.Failed++;
                return result;
            } else {
                result["error"] = JsonString(MalformedResponse);
            }
        } catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) {
            result["error"] = JsonString("Claim adoption could not be confirmed. Inspect the ledger before retrying.");
        }
        writes.Failed++;
        return result;
    }

    static JsonNode JsonString(string value) => JsonNode.Parse($"\"{JsonEncodedText.Encode(value)}\"")!;

    static List<JsonObject> OpenTasks(JsonObject plan) =>
        plan["tasks"] is JsonArray tasks
            ? tasks.OfType<JsonObject>()
                   .Where(t => Str(t["task_id"]) is not null && Str(t["status"]) is "in_progress" or "pending")
                   .OrderBy(t => t["ordinal"] is JsonValue v && v.TryGetValue(out int o) ? o : int.MaxValue)
                   .ToList()
            : [];

    /// <summary>
    /// A user-set status outranks an MCP write, so the server would refuse even an unchanged one. A
    /// partial task's note is withheld from this caller, and re-sending it as null would erase it.
    /// </summary>
    static JsonObject? Adoptable(List<JsonObject> open) {
        var mine = open.Where(t => Str(t["source"]) != "user" && !IsTrue(t["status_partial"])).ToList();

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
            if (response.StatusCode == HttpStatusCode.Unauthorized) writes.Unauthorized = true;
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

    static JsonArray? ParseArray(string text) {
        try { return JsonNode.Parse(text) as JsonArray; } catch { return null; }
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;

    static DateTimeOffset? Time(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    sealed class WriteCount {
        public int Attempted;
        public int Failed;
        public bool Unauthorized;
    }
}
