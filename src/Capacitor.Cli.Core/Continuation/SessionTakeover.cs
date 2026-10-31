using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Continuation;

/// <summary>A local exit record can authorize takeover even before the server observes the exit.
/// Claim ownership is independently admitted by the server; force never bypasses that fence.</summary>
public sealed class SessionTakeover(ISessionLivenessSource local, TimeProvider time) {
    /// <summary>The server's own threshold for treating an active session as stale.</summary>
    static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    const string MalformedResponse = "malformed response";

    /// <summary>The plans endpoint returns at most this many, the most recently touched, with no paging.</summary>
    public const int PlansReadCap = 20;

    /// <param name="skipAttached">Leaves out every work item the current session already holds and every plan it already
    /// works on: a declare or an adoption makes its item primary or its plan current over one the agent chose, so a
    /// rerun must not repeat what an earlier run wrote. When the current session's items cannot be read, nothing is
    /// written.</param>
    public async Task<TakeoverResult> RunAsync(
            HttpClient client, string baseUrl, string previous, string current, TakeoverAdmission admission,
            bool skipAttached = false, CancellationToken ct = default) {
        if (WorkContextIds.CanonicalSessionId(previous) is not { } previousWire || SessionId.Parse(previousWire) is not { } previousId)
            return new TakeoverResult.Refused($"'{previous}' is not a session id.", TakeoverResult.Refused.InvalidSessionId);

        if (WorkContextIds.CanonicalSessionId(current) is not { } currentWire)
            return new TakeoverResult.Refused($"'{current}' is not a session id.", TakeoverResult.Refused.InvalidSessionId);

        if (previousWire == currentWire)
            return new TakeoverResult.Refused(
                "A session cannot continue itself: name the session whose work this one takes over.", TakeoverResult.Refused.SelfContinuation);

        var outcome = new JsonObject { ["continued_from"] = previousWire };

        try {
            return await RunAsync(client, baseUrl, previous, previousWire, previousId, currentWire, admission, skipAttached, outcome, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new TakeoverResult.Interrupted(outcome, TakeoverResult.Interrupted.Cancelled);
        }
    }

    async Task<TakeoverResult> RunAsync(
            HttpClient client, string baseUrl, string previous, string previousWire, SessionId previousId, string currentWire,
            TakeoverAdmission admission, bool skipAttached, JsonObject outcome, CancellationToken ct) {
        var escaped = Uri.EscapeDataString(previousWire);

        var (summary, summaryError) = await ReadAsync(client, $"{baseUrl}/api/sessions/{escaped}/summary", ct);
        using var summaryResponse = summary;

        if (summary is null)
            return new TakeoverResult.Failed($"Reading session {previous} failed: {summaryError}");

        if (summary.StatusCode == HttpStatusCode.Unauthorized) return new TakeoverResult.Unauthorized();
        if (summary.StatusCode == HttpStatusCode.NotFound)
            return new TakeoverResult.Refused($"Session {previous} was not found or is not visible to you.", TakeoverResult.Refused.SourceNotFound);
        if (!summary.IsSuccessStatusCode)
            return new TakeoverResult.Failed($"Reading session {previous} failed: HTTP {(int)summary.StatusCode}.");

        local.Reap();

        var (liveness, refusal) = Judge(previous, local.Liveness(previousId), ParseObject(await summary.Content.ReadAsStringAsync(ct)), admission);
        if (refusal is not null) return refusal;

        outcome["liveness"] = liveness;

        var itemsRead = ReadAsync(client, $"{baseUrl}/api/work-items/session/{escaped}", ct);
        var plansRead = ReadAsync(client, $"{baseUrl}/api/sessions/{escaped}/plans", ct);
        var heldRead  = skipAttached ? ReadHeldWorkItemsAsync(client, baseUrl, currentWire, ct) : Task.FromResult<HashSet<string>?>([]);
        var (items, itemsError) = await itemsRead;
        var (plans, plansError) = await plansRead;
        var held = await heldRead;
        using var itemsResponse = items;
        using var plansResponse = plans;

        if (items?.StatusCode == HttpStatusCode.Unauthorized || plans?.StatusCode == HttpStatusCode.Unauthorized)
            return new TakeoverResult.Unauthorized();

        if (held is null)
            return new TakeoverResult.Failed($"Reading the work items session {currentWire} holds failed.", TakeoverResult.Failed.SuccessorUnreadable);

        // Every entry is listed before the first write, so an interrupted run still names what it never reached.
        var (workItems, declares) = await ReadWorkItemsAsync(items, itemsError, currentWire, ct);
        outcome["work_items"] = workItems;
        var adoptions = await ReadPlansAsync(baseUrl, plans, plansError, currentWire, skipAttached, outcome, ct);
        outcome["loose_end_claims"] = Unknown();

        var state = new RunState();

        foreach (var (entry, body) in declares) {
            if (state.Stopped(ct)) break;
            if (held.Contains(Str(body["work_item_id"]) ?? "")) {
                entry["attached"] = true;
                continue;
            }

            await WriteAsync(client, $"{baseUrl}/api/work-items/declare", body, entry, state, ct);
        }

        string? currentPlan = null;

        foreach (var (planId, url, body, entry) in adoptions) {
            if (state.Stopped(ct)) break;
            if (await WriteAsync(client, url, body, entry, state, ct)) currentPlan = planId;
        }

        if (currentPlan is not null) outcome["current_plan_id"] = currentPlan;

        if (!state.Stopped(ct)) outcome["loose_end_claims"] = await AdoptClaimsAsync(client, baseUrl, previousWire, currentWire, state, ct);

        if (state.Unauthorized) outcome["unauthorized"] = true;

        if (state.Interruption is { } code) return new TakeoverResult.Interrupted(outcome, code);

        var readFailed = Str(outcome["work_items"]?["status"]) == "failed" || outcome["plans_error"] is not null;

        return new TakeoverResult.Completed(outcome, Unsuccessful: state.Failed > 0 || readFailed, state.Unauthorized);
    }

    (string Liveness, TakeoverResult.Refused? Refusal) Judge(string previous, SessionLiveness here, JsonObject? summary, TakeoverAdmission admission) {
        var ended = string.Equals(Str(summary?["status"]), "ended", StringComparison.OrdinalIgnoreCase);

        switch (admission) {
            case TakeoverAdmission.Forced:
                return ("forced", null);
            case TakeoverAdmission.EndedOnly when here == SessionLiveness.Running:
                return ("running", Active($"Session {previous} is still running on this machine."));
            case TakeoverAdmission.EndedOnly:
                return ended ? ("ended", null) : ("active", new($"Session {previous} is no longer ended.", TakeoverResult.Refused.SourceNotEnded));
        }

        switch (here) {
            case SessionLiveness.Running:
                return ("running", Active($"Session {previous} is still running on this machine. Take it over only if the user confirms, by retrying with force."));
            case SessionLiveness.Exited:
                return ("exited", null);
        }

        if (ended) return ("ended", null);

        var lastSeen = Time(summary?["last_event_at"]) ?? Time(summary?["started_at"]);
        if (lastSeen is { } seen && time.GetUtcNow() - seen > StaleAfter) return ("stale", null);

        var when = lastSeen?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown";

        return ("active", Active($"Session {previous} still looks active (last event {when}) and did not run on this machine. If its agent is gone, ask the user, then retry with force."));
    }

    static TakeoverResult.Refused Active(string reason) => new(reason, TakeoverResult.Refused.SourceActive);

    static async Task<(JsonObject Section, List<(JsonObject Entry, JsonObject Body)> Declares)> ReadWorkItemsAsync(
            HttpResponseMessage? read, string? readError, string current, CancellationToken ct) {
        var declares = new List<(JsonObject, JsonObject)>();

        if (read is null)
            return (new JsonObject { ["status"] = "failed", ["error"] = readError, ["items"] = new JsonArray() }, declares);

        var body = await read.Content.ReadAsStringAsync(ct);

        if (read.StatusCode == HttpStatusCode.Forbidden && WorkContextReader.IsPlanGate((int)read.StatusCode, ParseWorkItemError(body)))
            return (new JsonObject { ["status"] = "not_in_plan", ["items"] = new JsonArray() }, declares);

        if (!read.IsSuccessStatusCode)
            return (new JsonObject { ["status"] = "failed", ["error"] = $"HTTP {(int)read.StatusCode}", ["items"] = new JsonArray() }, declares);

        if (ParseArray(body) is not { } list)
            return (new JsonObject { ["status"] = "failed", ["error"] = MalformedResponse, ["items"] = new JsonArray() }, declares);

        var attached = new JsonArray();

        // The last declare becomes primary, so the previous session's primary goes last.
        foreach (var item in list.OrderBy(i => IsTrue(i?["is_primary"])).ToList()) {
            if (Str(item?["work_item_id"]) is not { } workItemId) continue;

            var entry = new JsonObject { ["work_item_id"] = workItemId, ["label"] = Str(item?["label"]) ?? workItemId, ["attached"] = null };
            declares.Add((entry, new JsonObject { ["session_id"] = current, ["work_item_id"] = workItemId }));
            attached.Add((JsonNode?)entry);
        }

        return (new JsonObject { ["status"] = "ok", ["items"] = attached }, declares);
    }

    static async Task<List<(string PlanId, string Url, JsonObject Body, JsonObject Entry)>> ReadPlansAsync(
            string baseUrl, HttpResponseMessage? read, string? readError, string current, bool skipJoined, JsonObject outcome,
            CancellationToken ct) {
        var adoptions = new List<(string, string, JsonObject, JsonObject)>();
        var adopted   = new JsonArray();
        var skipped   = new JsonArray();
        outcome["plans"]         = adopted;
        outcome["skipped_plans"] = skipped;

        if (read is null || !read.IsSuccessStatusCode) {
            outcome["plans_error"] = read is null ? readError : $"HTTP {(int)read.StatusCode}";
            return adoptions;
        }

        if (ParseArray(await read.Content.ReadAsStringAsync(ct)) is not { } list) {
            outcome["plans_error"] = MalformedResponse;
            return adoptions;
        }

        if (list.Count >= PlansReadCap) outcome["plans_truncated"] = true;

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
                ["plan_id"] = planId, ["task_id"] = taskId, ["title"] = Str(task["title"]), ["status"] = status, ["attached"] = null,
            };

            // Status and note both go back unchanged: the server compares the two, so anything less
            // would record a change, and a dropped note would erase it.
            var update = new JsonObject { ["session_id"] = current, ["status"] = status };
            if (task["note"] is JsonValue noteValue && noteValue.TryGetValue(out string? note) && note is not null) update["note"] = note;

            adopted.Add((JsonNode?)entry);
            if (skipJoined && Joined(plan, current)) {
                entry["attached"] = true;
                continue;
            }

            var url = $"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks/{Uri.EscapeDataString(taskId)}";
            adoptions.Add((planId, url, update, entry));
        }

        return adoptions;
    }

    static bool Joined(JsonObject plan, string session) =>
        plan["sessions"] is JsonArray sessions && sessions.Any(s => WorkContextIds.CanonicalSessionId(Str(s)) == session);

    static async Task<JsonObject> AdoptClaimsAsync(
            HttpClient client, string baseUrl, string previous, string current, RunState writes, CancellationToken ct) {
        var result = (JsonObject)JsonNode.Parse("""{"status":"failed","results":[]}""")!;
        try {
            var body = $"{{\"previous_session_id\":\"{JsonEncodedText.Encode(previous)}\",\"session_id\":\"{JsonEncodedText.Encode(current)}\"}}";
            using var response = await client.PostAsync($"{baseUrl}/api/loose-ends/adopt", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var json = ParseObject(await response.Content.ReadAsStringAsync(ct));
            var code = Str(json?["code"]);
            if (response.StatusCode == HttpStatusCode.Conflict && code == TakeoverResult.Interrupted.ReservationLost)
                writes.Interruption = TakeoverResult.Interrupted.ReservationLost;
            if (!response.IsSuccessStatusCode) {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && code is null) {
                    result["status"] = JsonString("unsupported");
                    writes.Failed++;
                    return result;
                }
                if (code == "next_work_unavailable") result["status"] = JsonString("unavailable");
                result["error"] = JsonString($"HTTP {(int)response.StatusCode}" + (code is not null && NextWorkUntrustedText.IsCode(code) ? $" — {code}" : ""));
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
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            writes.Interruption = TakeoverResult.Interrupted.Cancelled;
            return Unknown();
        } catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException) {
            result["error"] = JsonString("Claim adoption could not be confirmed. Inspect the ledger before retrying.");
        }
        writes.Failed++;
        return result;
    }

    static JsonObject Unknown() => new() { ["status"] = "unknown" };

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

    /// <summary>A write cut off by <paramref name="ct"/> leaves <c>attached</c> null: the server may or may not have applied it.</summary>
    static async Task<bool> WriteAsync(HttpClient client, string url, JsonObject body, JsonObject entry, RunState writes, CancellationToken ct) {
        try {
            using var response = await client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

            if (response.IsSuccessStatusCode) {
                entry["attached"] = true;
                return true;
            }

            entry["error"] = $"HTTP {(int)response.StatusCode}";
            if (response.StatusCode == HttpStatusCode.Unauthorized) writes.Unauthorized = true;

            if (response.StatusCode == HttpStatusCode.Conflict &&
                Str(ParseObject(await response.Content.ReadAsStringAsync(ct))?["code"]) == TakeoverResult.Interrupted.ReservationLost) {
                entry["error"] = $"HTTP 409 — {TakeoverResult.Interrupted.ReservationLost}";
                writes.Interruption = TakeoverResult.Interrupted.ReservationLost;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            entry["attached"] = null;
            writes.Interruption = TakeoverResult.Interrupted.Cancelled;
            return false;
        } catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException) {
            entry["error"] = ex.Message;
        }

        entry["attached"] = false;
        writes.Failed++;

        return false;
    }

    /// <summary>The work items the session holds, or null when they cannot be read.</summary>
    static async Task<HashSet<string>?> ReadHeldWorkItemsAsync(HttpClient client, string baseUrl, string sessionWire, CancellationToken ct) {
        var (read, _) = await ReadAsync(client, $"{baseUrl}/api/work-items/session/{Uri.EscapeDataString(sessionWire)}", ct);
        using var response = read;
        if (response is not { IsSuccessStatusCode: true }) return null;

        return ParseArray(await response.Content.ReadAsStringAsync(ct)) is { } list
            ? [.. list.Select(i => Str(i?["work_item_id"])).OfType<string>()]
            : null;
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

    static WorkItemErrorDto? ParseWorkItemError(string text) {
        try { return JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.WorkItemErrorDto); } catch (JsonException) { return null; }
    }

    static JsonArray? ParseArray(string text) {
        try { return JsonNode.Parse(text) as JsonArray; } catch { return null; }
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;

    static DateTimeOffset? Time(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    sealed class RunState {
        public int Failed;
        public bool Unauthorized;
        public string? Interruption;

        public bool Stopped(CancellationToken ct) {
            if (Interruption is null && ct.IsCancellationRequested) Interruption = TakeoverResult.Interrupted.Cancelled;
            return Interruption is not null;
        }
    }
}
