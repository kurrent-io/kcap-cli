# Continue Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a fresh agent take over a dead session's work items and unfinished plans in one call — `kcap recap <X> --continue` or the `continue_session` MCP tool — refusing only when X may still be running.

**Architecture:** A local exit record (kept by `AgentSessions.Reap`) proves a session's process died, independent of any server or daemon connection. `SessionTakeover` composes existing server endpoints (session summary, work items, plans) behind that liveness check; the CLI flag and a new always-available `kcap-handoff` MCP server both call it. `get_session_summary` additionally lists a session's work items.

**Tech Stack:** .NET 10 NativeAOT, `System.Text.Json.Nodes` (no new `[JsonSerializable]` types), TUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-continue-session-design.md`

## Global Constraints

- Every takeover is available on every tier: nothing in the path may require a tier-gated feature. Work items answer `403 {"code":"work_items_not_in_plan"}` on Free; that is reported as `not_in_plan`, never as a failure.
- `kcap-sessions` stays read-only: no tool is added to it (`KcapMcpRegistry.ReviewFlowUnattendedSafeTools` pins its set).
- `kcap-handoff` is `AutoApprove: true`, `NeedsProjectCwd: false`, and is NOT added to `ReviewFlowAutoApprovableServers`.
- Plan adoption re-sends the task's current `status` AND `note`; tasks whose `source` is `user` are never chosen.
- Liveness order: `force` → proceed; local `Running` → refuse; local `Exited` → proceed; otherwise server `status == ended` → proceed, `last_event_at` older than 1 hour → proceed, else refuse.
- CLI refusal exits 2 and prints no recap; other takeover failures exit 1.
- Exit records live at `agent-sessions/exited/<SessionId.Value>` under the config root, content = ISO-8601 UTC time, retained 30 days.
- JSON is built with `JsonObject`/`new JsonArray()` + `.Add((JsonNode?)x)` — never a `JsonArray` collection expression (AOT).
- Comments: scarce, no history, no ticket ids, no Linear ids anywhere in `src/` or `test/` (CI runs `scripts/check-linear-ids.sh`).
- One type per file; namespace follows directory.
- Commit subjects ≤ 80 chars incl. ` (#1235)`, imperative, one clause; end each message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **X given as a dashed GUID while C is the 32-hex form** — the self-continue check must still refuse. Test in Task 2 (`Refuses_to_continue_itself_across_id_forms`).
2. **`/summary` without `last_event_at`** (a session that never produced an event) — judge staleness by `started_at`, like the server. Test in Task 2 (`Unknown_liveness_falls_back_to_started_at`).
3. **A plan whose tasks lack `task_id` or carry an unknown status** — never adopted, never crash. Test in Task 2 (`Tasks_without_ids_are_not_adopted`).
4. **A network exception on one write** (not an HTTP status) — reported on that entry; the other writes still run. Test in Task 2 (`A_throwing_write_is_reported_and_the_rest_continue`).
5. **`kcap recap --continue` with no positional id inside a harness session** — must be a usage error, never "continue the current session from itself" via env fallback. Test in Task 4 (`PositionalSessionId_ignores_the_environment`).

---

### Task 1: Local exit records in `AgentSessions`

**Files:**
- Create: `src/Capacitor.Cli/SessionLiveness.cs`
- Modify: `src/Capacitor.Cli/AgentSessions.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentSessionsTests.cs`

**Interfaces:**
- Produces: `enum SessionLiveness { Unknown, Running, Exited }` (namespace `Capacitor.Cli`); `AgentSessions(ConfigRoot config, Func<int, int?> parentOf, TimeProvider? time = null)`; `SessionLiveness AgentSessions.Liveness(SessionId session)`; `Reap()` now records exits and prunes old records.

- [ ] **Step 1: Write the failing tests** — append to `AgentSessionsTests`:

```csharp
    string ExitRecord(string session) => Config.Root.Path("agent-sessions", "exited", session);

    /// <summary>A note left by a process that is gone: the token names another boot.</summary>
    void DeadNote(int pid, string session) {
        Directory.CreateDirectory(Path.GetDirectoryName(Note(pid))!);
        File.WriteAllText(Note(pid), $"{session}\nlx:another-boot:1");
    }

    [Test]
    public async Task Reap_keeps_an_exit_record_for_a_dead_claim() {
        DeadNote(Shell, "gone");

        Sessions.Reap();

        await Assert.That(File.Exists(Note(Shell))).IsFalse();
        await Assert.That(Sessions.Liveness(SessionId.Parse("gone")!)).IsEqualTo(SessionLiveness.Exited);
    }

    [Test]
    public async Task A_live_claim_is_running_even_with_an_exit_record() {
        DeadNote(Shell, Session.Value);
        Sessions.Reap();
        Sessions.Claim(Agent, Session);

        await Assert.That(Sessions.Liveness(Session)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task A_session_nothing_here_ran_is_unknown() {
        await Assert.That(Sessions.Liveness(SessionId.Parse("elsewhere")!)).IsEqualTo(SessionLiveness.Unknown);
    }

    [Test]
    public async Task Reap_prunes_exit_records_past_retention() {
        Directory.CreateDirectory(Path.GetDirectoryName(ExitRecord("old"))!);
        File.WriteAllText(ExitRecord("old"), DateTimeOffset.UtcNow.AddDays(-31).ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(ExitRecord("recent"), DateTimeOffset.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(ExitRecord("garbled"), "not a time");

        Sessions.Reap();

        await Assert.That(File.Exists(ExitRecord("old"))).IsFalse();
        await Assert.That(File.Exists(ExitRecord("garbled"))).IsFalse();
        await Assert.That(File.Exists(ExitRecord("recent"))).IsTrue();
    }

    [Test]
    public async Task Exit_records_are_not_read_as_claims() {
        DeadNote(Shell, "gone");
        Sessions.Reap();
        Sessions.Claim(Agent, Session);

        await Assert.That(Sessions.Above(Hook)).IsEqualTo(Session);
        await Assert.That(Sessions.IsClaimed(SessionId.Parse("gone")!)).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentSessionsTests/*"`
Expected: build error — `SessionLiveness` / `Liveness` do not exist.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli/SessionLiveness.cs`:

```csharp
namespace Capacitor.Cli;

/// <summary>What this machine knows about a session's agent process.</summary>
enum SessionLiveness {
    /// <summary>No process here ran it, or its record has been pruned.</summary>
    Unknown,
    Running,
    Exited
}
```

In `AgentSessions.cs`: change the primary constructor and add members (keep every existing member unchanged except `Reap`):

```csharp
sealed class AgentSessions(ConfigRoot config, Func<int, int?> parentOf, TimeProvider? time = null) {
    const int MaxHops = 32;

    static readonly TimeSpan ExitRetention = TimeSpan.FromDays(30);

    readonly TimeProvider _time = time ?? TimeProvider.System;
```

Replace `Reap` and its doc comment:

```csharp
    /// <summary>
    /// Drops every note no live process holds, keeping an exit record for its session: the only local
    /// proof a session's agent is gone when nothing told the server, as for a private daemon agent.
    /// </summary>
    public void Reap() {
        foreach (var pid in Claimants()) {
            if (Of(pid) is not null) continue;

            try {
                if (NotedSession(pid) is { } session) RecordExit(session);
                File.Delete(Note(pid));
            } catch { }
        }

        PruneExitRecords();
    }

    /// <summary>
    /// A live claim wins over an exit record: a session resumed in a new process is running again.
    /// </summary>
    public SessionLiveness Liveness(SessionId session) =>
        IsClaimed(session)              ? SessionLiveness.Running
      : File.Exists(ExitRecord(session)) ? SessionLiveness.Exited
      : SessionLiveness.Unknown;

    SessionId? NotedSession(int pid) =>
        File.ReadAllText(Note(pid)).Split('\n') is [var session, _] ? SessionId.Parse(session) : null;

    void RecordExit(SessionId session) {
        var record = ExitRecord(session);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);

        var temp = $"{record}.{Environment.ProcessId}";
        File.WriteAllText(temp, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        File.Move(temp, record, overwrite: true);
    }

    void PruneExitRecords() {
        var cutoff = _time.GetUtcNow() - ExitRetention;

        try {
            foreach (var record in Directory.EnumerateFiles(config.Path("agent-sessions", "exited"))) {
                try {
                    if (!DateTimeOffset.TryParse(File.ReadAllText(record), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at < cutoff)
                        File.Delete(record);
                } catch { }
            }
        } catch { }
    }

    string ExitRecord(SessionId session) => config.Path("agent-sessions", "exited", session.Value);
```

`Claimants()` enumerates files only, so the `exited` directory is never read as a pid; leave it as is.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentSessionsTests/*"`
Expected: all AgentSessionsTests pass (the two existing ones included).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/SessionLiveness.cs src/Capacitor.Cli/AgentSessions.cs test/Capacitor.Cli.Tests.Unit/AgentSessionsTests.cs
git commit -m "Keep a local exit record when an agent's process is gone (#1235)" -m "A private daemon agent's death never reaches the server; this record is the only proof left." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `SessionTakeover` core

**Files:**
- Create: `src/Capacitor.Cli/Continuation/SessionTakeover.cs`
- Create: `src/Capacitor.Cli/Continuation/TakeoverResult.cs`
- Create: `src/Capacitor.Cli/Continuation/DeclaredPlanState.cs`
- Create: `src/Capacitor.Cli/Continuation/TakeoverReport.cs`
- Modify: `src/Capacitor.Cli/Commands/McpSessionsServer.cs` (`ProjectDeclaredPlans` uses `DeclaredPlanState.IsFinished`)
- Test: `test/Capacitor.Cli.Tests.Unit/Continuation/SessionTakeoverTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Continuation/TakeoverReportTests.cs`

**Interfaces:**
- Consumes: `AgentSessions.Liveness`, `AgentSessions.Reap`, `SessionLiveness` (Task 1).
- Produces (namespace `Capacitor.Cli.Continuation`):
  - `sealed class SessionTakeover(AgentSessions local, TimeProvider time)` with `Task<TakeoverResult> RunAsync(HttpClient client, string baseUrl, string previous, string current, bool force, CancellationToken ct = default)`.
  - `abstract record TakeoverResult` with nested `Refused(string Reason)`, `Unauthorized()`, `Failed(string Reason)`, `Completed(JsonObject Outcome, bool AllWritesFailed)`.
  - `static class DeclaredPlanState` with `bool IsFinished(JsonNode? plan)`.
  - `static class TakeoverReport` with `string Render(JsonObject outcome)` — Markdown for the CLI.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Continuation/SessionTakeoverTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Continuation;

public class SessionTakeoverTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Current  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string Base     = "http://x";

    AgentSessions Local => field ??= new(Config.Root, _ => null);

    static string Ended() => """{"session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"ended"}""";

    static string Active(TimeSpan ago) =>
        $$"""{"session_id":"{{Previous}}","status":"active","started_at":"{{DateTimeOffset.UtcNow.AddHours(-5):O}}","last_event_at":"{{DateTimeOffset.UtcNow - ago:O}}"}""";

    static string Plan(string id, bool current, string tasksJson, bool finished = false) =>
        $$"""{"plan_id":"{{id}}","is_current":{{(current ? "true" : "false")}},"is_complete":true,"progress":{"completed":0,"total":2,"total_known":true,"finished":{{(finished ? "true" : "false")}}},"tasks":{{tasksJson}}}""";

    static string Task(string id, int ordinal, string status, string source = "mcp", string? note = null) =>
        $$"""{"task_id":"{{id}}","ordinal":{{ordinal}},"title":"T{{ordinal}}","status":"{{status}}","source":"{{source}}","note":{{(note is null ? "null" : $"\"{note}\"")}}}""";

    Routes Server(string summary, string? items = "[]", string? plans = "[]") {
        var routes = new Routes();
        routes.Get($"/api/sessions/{Previous}/summary", 200, summary);
        if (items is not null) routes.Get($"/api/work-items/session/{Previous}", 200, items);
        if (plans is not null) routes.Get($"/api/sessions/{Previous}/plans", 200, plans);
        return routes;
    }

    async Task<TakeoverResult> Run(Routes routes, bool force = false, string previous = Previous, string current = Current) {
        using var client = new HttpClient(routes);
        return await new SessionTakeover(Local, TimeProvider.System).RunAsync(client, Base, previous, current, force);
    }

    static JsonObject Outcome(TakeoverResult r) => ((TakeoverResult.Completed)r).Outcome;

    void DeadClaim(string session) {
        var note = Config.Root.Path("agent-sessions", "900001");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, $"{session}\nlx:another-boot:1");
    }

    // ── refusals ──

    [Test]
    public async Task Refuses_to_continue_itself_across_id_forms() {
        var r = await Run(new Routes(), previous: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", current: Previous);
        await Assert.That(r).IsTypeOf<TakeoverResult.Refused>();
    }

    [Test]
    public async Task Refuses_a_session_the_server_does_not_show() {
        var routes = new Routes();
        routes.Get($"/api/sessions/{Previous}/summary", 404, "");
        await Assert.That(await Run(routes)).IsTypeOf<TakeoverResult.Refused>();
    }

    [Test]
    public async Task A_401_on_the_summary_is_unauthorized() {
        var routes = new Routes();
        routes.Get($"/api/sessions/{Previous}/summary", 401, "");
        await Assert.That(await Run(routes)).IsTypeOf<TakeoverResult.Unauthorized>();
    }

    [Test]
    public async Task Refuses_while_a_live_process_here_runs_it() {
        Local.Claim(Environment.ProcessId, SessionId.Parse(Previous)!);
        var routes = Server(Ended());

        var r = await Run(routes);

        await Assert.That(r).IsTypeOf<TakeoverResult.Refused>();
        await Assert.That(((TakeoverResult.Refused)r).Reason).Contains("running on this machine");
        await Assert.That(routes.Posts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Refuses_a_recently_active_session_with_no_local_record() {
        var r = await Run(Server(Active(TimeSpan.FromMinutes(5))));
        await Assert.That(r).IsTypeOf<TakeoverResult.Refused>();
        await Assert.That(((TakeoverResult.Refused)r).Reason).Contains("force");
    }

    [Test]
    public async Task Force_overrides_a_live_local_claim() {
        Local.Claim(Environment.ProcessId, SessionId.Parse(Previous)!);
        var r = await Run(Server(Active(TimeSpan.FromMinutes(5))), force: true);
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("forced");
    }

    // ── liveness that proceeds ──

    [Test]
    public async Task A_local_exit_record_proceeds_even_when_the_server_says_active() {
        DeadClaim(Previous);
        var r = await Run(Server(Active(TimeSpan.FromMinutes(1))));
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("exited");
    }

    [Test]
    public async Task An_ended_session_proceeds() {
        var r = await Run(Server(Ended()));
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("ended");
    }

    [Test]
    public async Task An_active_session_idle_past_an_hour_proceeds() {
        var r = await Run(Server(Active(TimeSpan.FromHours(2))));
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("stale");
    }

    [Test]
    public async Task Unknown_liveness_falls_back_to_started_at() {
        var summary = $$"""{"session_id":"{{Previous}}","status":"active","started_at":"{{DateTimeOffset.UtcNow.AddHours(-3):O}}"}""";
        var r = await Run(Server(summary));
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("stale");
    }

    // ── work items ──

    [Test]
    public async Task Attaches_every_work_item_to_the_current_session() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"#1 — One","is_primary":true},{"work_item_id":"w2","label":"Two","is_primary":false}]""");
        routes.Post("/api/work-items/declare", 200, "{}");

        var o = Outcome(await Run(routes));

        var declares = routes.Posts.Where(p => p.Path == "/api/work-items/declare").ToList();
        await Assert.That(declares.Count).IsEqualTo(2);
        await Assert.That(declares[0].Body!["session_id"]!.GetValue<string>()).IsEqualTo(Current);
        await Assert.That(declares[0].Body!["work_item_id"]!.GetValue<string>()).IsEqualTo("w1");
        await Assert.That(o["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("ok");
        await Assert.That(o["work_items"]!["items"]!.AsArray().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Work_items_outside_the_plan_are_reported_and_plans_are_still_adopted() {
        var routes = Server(Ended(), items: null, plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "in_progress")}]")}]");
        routes.Get($"/api/work-items/session/{Previous}", 403, """{"code":"work_items_not_in_plan","message":"Work Items require the Team or Enterprise plan."}""");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = await Run(routes);

        await Assert.That(r).IsTypeOf<TakeoverResult.Completed>();
        await Assert.That(((TakeoverResult.Completed)r).AllWritesFailed).IsFalse();
        await Assert.That(Outcome(r)["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("not_in_plan");
        await Assert.That(Outcome(r)["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
    }

    // ── plans ──

    [Test]
    public async Task Adopts_the_in_progress_task_resending_its_status_and_note() {
        var tasks = $"[{Task("t1", 1, "pending")},{Task("t2", 2, "in_progress", note: "half done")}]";
        var routes = Server(Ended(), plans: $"[{Plan("p1", true, tasks)}]");
        routes.Post("/api/plans/p1/tasks/t2", 200, "{}");

        var o = Outcome(await Run(routes));

        var body = routes.Posts.Single(p => p.Path == "/api/plans/p1/tasks/t2").Body!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Current);
        await Assert.That(body["status"]!.GetValue<string>()).IsEqualTo("in_progress");
        await Assert.That(body["note"]!.GetValue<string>()).IsEqualTo("half done");
        await Assert.That(o["plans"]!.AsArray()[0]!["task_id"]!.GetValue<string>()).IsEqualTo("t2");
    }

    [Test]
    public async Task Falls_back_to_the_first_pending_task_and_skips_user_set_ones() {
        var tasks = $"[{Task("t1", 1, "completed")},{Task("t2", 2, "pending", source: "user")},{Task("t3", 3, "pending")}]";
        var routes = Server(Ended(), plans: $"[{Plan("p1", true, tasks)}]");
        routes.Post("/api/plans/p1/tasks/t3", 200, "{}");

        await Run(routes);

        await Assert.That(routes.Posts.Single().Path).IsEqualTo("/api/plans/p1/tasks/t3");
    }

    [Test]
    public async Task Finished_plans_and_plans_without_an_adoptable_task_are_skipped() {
        var plans = "[" + string.Join(",",
            Plan("done", false, $"[{Task("a", 1, "completed")}]", finished: true),
            Plan("closed", false, $"[{Task("b", 1, "skipped")}]"),
            Plan("mine", false, $"[{Task("c", 1, "in_progress", source: "user")}]")) + "]";

        var o = Outcome(await Run(Server(Ended(), plans: plans)));

        var reasons = o["skipped_plans"]!.AsArray().ToDictionary(n => n!["plan_id"]!.GetValue<string>(), n => n!["reason"]!.GetValue<string>());
        await Assert.That(reasons["done"]).IsEqualTo("finished");
        await Assert.That(reasons["closed"]).IsEqualTo("no_open_task");
        await Assert.That(reasons["mine"]).IsEqualTo("user_owned");
    }

    [Test]
    public async Task Tasks_without_ids_are_not_adopted() {
        var tasks = """[{"ordinal":1,"title":"x","status":"in_progress","source":"mcp"},{"task_id":"t2","ordinal":2,"title":"y","status":"weird","source":"mcp"}]""";
        var o = Outcome(await Run(Server(Ended(), plans: $"[{Plan("p1", true, tasks)}]")));
        await Assert.That(o["skipped_plans"]!.AsArray()[0]!["reason"]!.GetValue<string>()).IsEqualTo("no_open_task");
    }

    [Test]
    public async Task The_previous_sessions_current_plan_is_adopted_last() {
        var plans = $"[{Plan("cur", true, $"[{Task("a", 1, "pending")}]")},{Plan("other", false, $"[{Task("b", 1, "pending")}]")}]";
        var routes = Server(Ended(), plans: plans);
        routes.Post("/api/plans/cur/tasks/a", 200, "{}");
        routes.Post("/api/plans/other/tasks/b", 200, "{}");

        var o = Outcome(await Run(routes));

        await Assert.That(routes.Posts.Last().Path).IsEqualTo("/api/plans/cur/tasks/a");
        await Assert.That(o["current_plan_id"]!.GetValue<string>()).IsEqualTo("cur");
    }

    // ── failures ──

    [Test]
    public async Task A_failed_write_is_reported_on_its_entry_and_is_not_a_failure_overall() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Post("/api/work-items/declare", 500, "boom");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.AllWritesFailed).IsFalse();
        var item = r.Outcome["work_items"]!["items"]!.AsArray()[0]!;
        await Assert.That(item["attached"]!.GetValue<bool>()).IsFalse();
        await Assert.That(item["error"]!.GetValue<string>()).Contains("500");
    }

    [Test]
    public async Task A_throwing_write_is_reported_and_the_rest_continue() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Throw("/api/work-items/declare");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Outcome["work_items"]!["items"]!.AsArray()[0]!["attached"]!.GetValue<bool>()).IsFalse();
        await Assert.That(r.Outcome["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
    }

    [Test]
    public async Task Every_write_failing_is_a_failure() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""");
        routes.Post("/api/work-items/declare", 500, "boom");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.AllWritesFailed).IsTrue();
    }

    [Test]
    public async Task A_failed_plans_read_is_reported() {
        var routes = Server(Ended(), plans: null);
        routes.Get($"/api/sessions/{Previous}/plans", 500, "boom");

        var o = Outcome(await Run(routes));

        await Assert.That(o["plans_error"]!.GetValue<string>()).Contains("500");
    }

    /// <summary>An in-memory server: unrouted requests answer 404 so a missing route fails the test
    /// loudly rather than looking like an empty answer.</summary>
    sealed class Routes : HttpMessageHandler {
        readonly Dictionary<(HttpMethod, string), (int Status, string Body)> _routes = [];
        readonly HashSet<string> _throwing = [];

        public List<(string Path, JsonObject? Body)> Posts { get; } = [];

        public void Get(string path, int status, string body)  => _routes[(HttpMethod.Get, path)]  = (status, body);
        public void Post(string path, int status, string body) => _routes[(HttpMethod.Post, path)] = (status, body);
        public void Throw(string path) => _throwing.Add(path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Post) {
                var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                Posts.Add((path, text is null ? null : JsonNode.Parse(text) as JsonObject));
            }

            if (_throwing.Contains(path)) throw new HttpRequestException("connection reset");

            return _routes.TryGetValue((request.Method, path), out var r)
                ? new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new StringContent(r.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        }
    }
}
```

`test/Capacitor.Cli.Tests.Unit/Continuation/TakeoverReportTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Continuation;

namespace Capacitor.Cli.Tests.Unit.Continuation;

public class TakeoverReportTests {
    [Test]
    public async Task Names_what_was_attached_skipped_and_now_current() {
        var outcome = JsonNode.Parse("""
            {"continued_from":"aaa","liveness":"exited",
             "work_items":{"status":"ok","items":[{"work_item_id":"w1","label":"#1 — One","attached":true},{"work_item_id":"w2","label":"Two","attached":false,"error":"HTTP 500"}]},
             "plans":[{"plan_id":"p1","task_id":"t1","title":"Step one","status":"in_progress","attached":true}],
             "skipped_plans":[{"plan_id":"p2","reason":"finished"}],
             "current_plan_id":"p1"}
            """)!.AsObject();

        var text = TakeoverReport.Render(outcome);

        await Assert.That(text).StartsWith("## Continued from session aaa");
        await Assert.That(text).Contains("#1 — One");
        await Assert.That(text).Contains("Two — not attached: HTTP 500");
        await Assert.That(text).Contains("p1: task t1 \"Step one\" (in_progress)");
        await Assert.That(text).Contains("p2 — skipped: finished");
        await Assert.That(text).Contains("Current plan: p1");
    }

    [Test]
    public async Task Says_when_work_items_are_not_in_the_plan() {
        var outcome = JsonNode.Parse("""{"continued_from":"aaa","liveness":"ended","work_items":{"status":"not_in_plan","items":[]},"plans":[],"skipped_plans":[]}""")!.AsObject();

        await Assert.That(TakeoverReport.Render(outcome)).Contains("Work items: not available on this plan");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/Capacitor.Cli.Tests.Unit.Continuation/*/*"`
Expected: build error — namespace `Capacitor.Cli.Continuation` does not exist.

- [ ] **Step 3: Implement**

`src/Capacitor.Cli/Continuation/TakeoverResult.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

abstract record TakeoverResult {
    TakeoverResult() { }

    /// <summary>Nothing was written: the session may still be running, or cannot be continued.</summary>
    public sealed record Refused(string Reason) : TakeoverResult;

    public sealed record Unauthorized : TakeoverResult;

    /// <summary>The previous session could not be read, so nothing was attempted.</summary>
    public sealed record Failed(string Reason) : TakeoverResult;

    public sealed record Completed(JsonObject Outcome, bool AllWritesFailed) : TakeoverResult;
}
```

`src/Capacitor.Cli/Continuation/DeclaredPlanState.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

static class DeclaredPlanState {
    /// <summary>The server's <c>progress.finished</c> when sent; otherwise derived, with
    /// total_known part of it because a plan with no declared task list also reads 0 of 0.</summary>
    public static bool IsFinished(JsonNode? plan) {
        var progress = plan?["progress"];

        if (progress?["finished"] is JsonValue sent && sent.TryGetValue(out bool fromServer)) return fromServer;

        return IsTrue(progress?["total_known"])
            && IntOrZero(progress?["completed"]) == IntOrZero(progress?["total"])
            && IsTrue(plan?["is_complete"]);
    }

    static int  IntOrZero(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int i) ? i : 0;
    static bool IsTrue(JsonNode? node)    => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
```

In `McpSessionsServer.ProjectDeclaredPlans`, replace the local `finished` computation with `var finished = DeclaredPlanState.IsFinished(plan);` (add `using Capacitor.Cli.Continuation;`). Keep the rest of the projection as is; run the existing `McpSessionsServerTests` in Step 4 to prove nothing moved.

`src/Capacitor.Cli/Continuation/SessionTakeover.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

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

        var escaped = Uri.EscapeDataString(previous);

        using var summary = await client.GetAsync($"{baseUrl}/api/sessions/{escaped}/summary", ct);

        if (summary.StatusCode == HttpStatusCode.Unauthorized) return new TakeoverResult.Unauthorized();
        if (summary.StatusCode == HttpStatusCode.NotFound)
            return new TakeoverResult.Refused($"Session {previous} was not found or is not visible to you.");
        if (!summary.IsSuccessStatusCode)
            return new TakeoverResult.Failed($"Reading session {previous} failed: HTTP {(int)summary.StatusCode}.");

        local.Reap();

        var (liveness, refusal) = Judge(previous, local.Liveness(previousId), ParseObject(await summary.Content.ReadAsStringAsync(ct)), force);
        if (refusal is not null) return new TakeoverResult.Refused(refusal);

        var itemsRead = client.GetAsync($"{baseUrl}/api/work-items/session/{escaped}", ct);
        var plansRead = client.GetAsync($"{baseUrl}/api/sessions/{escaped}/plans", ct);
        using var items = await itemsRead;
        using var plans = await plansRead;

        var writes  = new WriteCount();
        var outcome = new JsonObject {
            ["continued_from"] = previous,
            ["liveness"]       = liveness,
            ["work_items"]     = await AttachWorkItemsAsync(client, baseUrl, items, current, writes, ct),
        };

        await AdoptPlansAsync(client, baseUrl, plans, current, outcome, writes, ct);

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
            HttpClient client, string baseUrl, HttpResponseMessage read, string current, WriteCount writes, CancellationToken ct) {
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
            HttpClient client, string baseUrl, HttpResponseMessage read, string current, JsonObject outcome, WriteCount writes, CancellationToken ct) {
        var adopted = new JsonArray();
        var skipped = new JsonArray();
        outcome["plans"]         = adopted;
        outcome["skipped_plans"] = skipped;

        if (!read.IsSuccessStatusCode) {
            outcome["plans_error"] = $"HTTP {(int)read.StatusCode}";
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
            if (Str(task["note"]) is { } note) update["note"] = note;

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
        } catch (HttpRequestException ex) {
            entry["error"] = ex.Message;
        }

        entry["attached"] = false;
        writes.Failed++;

        return false;
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
```

`WriteCount` is a private nested helper of `SessionTakeover`, not a public type, so the one-type-per-file rule is kept.

`src/Capacitor.Cli/Continuation/TakeoverReport.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

/// <summary>The takeover outcome as Markdown, printed by <c>kcap recap --continue</c> ahead of the recap.</summary>
static class TakeoverReport {
    public static string Render(JsonObject outcome) {
        var sb = new StringBuilder();
        sb.AppendLine($"## Continued from session {Str(outcome["continued_from"])}");
        sb.AppendLine();

        var workItems = outcome["work_items"];
        switch (Str(workItems?["status"])) {
            case "not_in_plan":
                sb.AppendLine("Work items: not available on this plan.");
                break;
            case "failed":
                sb.AppendLine($"Work items: could not be read ({Str(workItems?["error"])}).");
                break;
            default:
                var items = workItems?["items"] as JsonArray;
                sb.AppendLine(items is { Count: > 0 } ? "Work items:" : "Work items: none.");
                foreach (var item in items ?? new JsonArray())
                    sb.AppendLine(IsTrue(item?["attached"])
                        ? $"- {Str(item?["label"])}"
                        : $"- {Str(item?["label"])} — not attached: {Str(item?["error"])}");
                break;
        }

        sb.AppendLine();

        if (Str(outcome["plans_error"]) is { } plansError) {
            sb.AppendLine($"Plans: could not be read ({plansError}).");
        } else {
            var plans   = outcome["plans"] as JsonArray ?? new JsonArray();
            var skipped = outcome["skipped_plans"] as JsonArray ?? new JsonArray();
            sb.AppendLine(plans.Count + skipped.Count > 0 ? "Plans:" : "Plans: none.");

            foreach (var plan in plans) {
                var line = $"- {Str(plan?["plan_id"])}: task {Str(plan?["task_id"])} \"{Str(plan?["title"])}\" ({Str(plan?["status"])})";
                sb.AppendLine(IsTrue(plan?["attached"]) ? line : $"{line} — not attached: {Str(plan?["error"])}");
            }

            foreach (var plan in skipped)
                sb.AppendLine($"- {Str(plan?["plan_id"])} — skipped: {Str(plan?["reason"])?.Replace('_', ' ')}");
        }

        if (Str(outcome["current_plan_id"]) is { } current) {
            sb.AppendLine();
            sb.AppendLine($"Current plan: {current}");
        }

        return sb.ToString();
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
```

Note for the report test: `"finished"` renders as `skipped: finished`; `no_open_task` renders as `skipped: no open task`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/Capacitor.Cli.Tests.Unit.Continuation/*/*"`
Expected: all pass.

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all pass (the `finished` refactor changed nothing).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Continuation src/Capacitor.Cli/Commands/McpSessionsServer.cs test/Capacitor.Cli.Tests.Unit/Continuation
git commit -m "Add the session takeover behind a local-first liveness check (#1235)" -m "Adoption re-sends each task's status and note: the server records anything else as a change." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `kcap-handoff` MCP server

**Files:**
- Create: `src/Capacitor.Cli/Commands/McpHandoffServer.cs`
- Modify: `src/Capacitor.Cli.Core/Mcp/KcapMcpServers.cs` (`All`)
- Modify: `src/Capacitor.Cli.Core/KcapMcpRegistry.cs` (`Entries`)
- Modify: `src/Capacitor.Cli/Program.cs` (`mcp` switch, next to `case "plans":`)
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs` (after `AddTransient<McpPlansServer>()`)
- Modify: `kcap/.mcp.json`
- Modify: `src/Capacitor.Cli.Core/Resources/help-mcp.txt`, `kcap/README.md`, `README.md`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/McpHandoffServerTests.cs`

**Interfaces:**
- Consumes: `SessionTakeover`, `TakeoverResult` (Task 2); `AgentSessions.OnThisMachine(ConfigRoot)`.
- Produces: `sealed class McpHandoffServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http, TelemetryStartup startup, TimeProvider time)` with `Task<int> RunAsync()`, `internal Task<string> HandleToolCallAsync(JsonNode id, JsonObject request, HttpClient client, string baseUrl, string? currentSessionId)`, `internal static McpTool[] BuildToolsList()`, `internal const string ServerInstructions`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Commands/McpHandoffServerTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpHandoffServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Current  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    McpHandoffServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup, TimeProvider.System);

    async Task<string> Call(string argsJson, HttpMessageHandler handler, string? current = Current) {
        using var client = new HttpClient(handler);
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = "continue_session", ["arguments"] = JsonNode.Parse(argsJson) }
        };
        return await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x", current);
    }

    static string Text(string response) => JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    static bool IsError(string response) => JsonNode.Parse(response)!["result"]!["isError"]?.GetValue<bool>() == true;

    [Test]
    public async Task Advertises_only_continue_session() {
        var tools = McpHandoffServer.BuildToolsList();
        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(new[] { "continue_session" });
        await Assert.That(tools[0].InputSchema.Required).IsEquivalentTo(new[] { "session_id" });
        await Assert.That(tools[0].InputSchema.Properties.Keys).Contains("force");
    }

    [Test]
    public async Task Is_registered_auto_approved_and_kept_away_from_reviewers() {
        var server = KcapMcpServers.All.Single(s => s.Name == "kcap-handoff");
        await Assert.That(server.AutoApprove).IsTrue();
        await Assert.That(server.Args).IsEquivalentTo(new[] { "mcp", "handoff" }, CollectionOrdering.Matching);
        await Assert.That(KcapMcpRegistry.ReviewFlowAutoApprovableServers.Contains("kcap-handoff")).IsFalse();
    }

    [Test]
    public async Task Returns_the_outcome_as_json() {
        var handler = new Answers(req => req.RequestUri!.AbsolutePath switch {
            $"/api/sessions/{Previous}/summary" => (200, """{"status":"ended"}"""),
            _                                   => (200, "[]"),
        });

        var response = await Call($$"""{"session_id":"{{Previous}}"}""", handler);

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(JsonNode.Parse(Text(response))!["continued_from"]!.GetValue<string>()).IsEqualTo(Previous);
    }

    [Test]
    public async Task A_refusal_is_a_tool_error_carrying_the_reason() {
        var response = await Call($$"""{"session_id":"{{Current}}"}""", new Answers(_ => (200, "{}")));

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("cannot continue itself");
    }

    [Test]
    public async Task No_current_session_is_a_tool_error() {
        var response = await Call($$"""{"session_id":"{{Previous}}"}""", new Answers(_ => (200, "{}")), current: null);

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("CLAUDE_CODE_SESSION_ID");
    }

    [Test]
    public async Task Force_must_be_a_boolean() {
        var response = await Call($$"""{"session_id":"{{Previous}}","force":"yes"}""", new Answers(_ => (200, "{}")));

        await Assert.That(IsError(response)).IsTrue();
    }

    sealed class Answers(Func<HttpRequestMessage, (int Status, string Body)> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpHandoffServerTests/*"`
Expected: build error — `McpHandoffServer` does not exist.

- [ ] **Step 3: Implement the server**

`src/Capacitor.Cli/Commands/McpHandoffServer.cs` — the stdio loop, telemetry, lazy client and the JSON-RPC helpers (`DecodeMethod`, `BuildToolResult`, `BuildErrorResponse`, `ToResponse`, `BuildInitializeResponse`, `BuildToolsListResponse`) are copied verbatim from `McpPlansServer` (lines 29–130 and its helper block), with `"kcap-plans"` → `"kcap-handoff"` and `"kcap mcp plans"` → `"kcap mcp handoff"`. The parts that differ:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>Continues a session whose agent is gone. Kept out of every tier-gated server, because
/// continuing must work on every plan, and out of kcap-sessions, which reviewers auto-approve whole.</summary>
sealed class McpHandoffServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    internal const string ToolName = "continue_session";

    internal const string NoCurrentSessionMessage =
        "Error: no current session. continue_session must run inside the harness session that takes over (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;
        var current = WorkContextIds.CanonicalSessionId(HarnessRequesterContext.Resolve().SessionId);
        // ... copied loop from McpPlansServer; DispatchToolCallAsync calls
        // HandleToolCallAsync(callId, callRequest, client, baseUrl, current)
        // and mcp.ToolCalled("kcap-handoff", ...).
    }

    internal const string ServerInstructions =
        "Use continue_session when the user asks you to continue, resume or pick up another session's work — " +
        "typically one whose agent was killed. It attaches this session to that session's work items and " +
        "unfinished plans. If it refuses because the session may still be running, ask the user before " +
        "retrying with force: true. Then check the working tree before resuming: a task left in progress may be half-done.";

    internal async Task<string> HandleToolCallAsync(JsonNode id, JsonObject request, HttpClient client, string baseUrl, string? currentSessionId) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) return BuildErrorResponse(id, -32602, "Missing params.name");
        if (toolName != ToolName) return BuildToolResult(id, $"Error: Unknown tool: {toolName}", isError: true);

        try {
            if (currentSessionId is null) return BuildToolResult(id, NoCurrentSessionMessage, isError: true);

            var previous = McpToolArguments.RequireString(arguments, "session_id");
            var force    = OptionalBool(arguments, "force");

            var result = await new SessionTakeover(AgentSessions.OnThisMachine(config), time)
                .RunAsync(client, baseUrl, previous, currentSessionId, force);

            return result switch {
                TakeoverResult.Completed c  => BuildToolResult(id, c.Outcome.ToJsonString(), isError: c.AllWritesFailed),
                TakeoverResult.Refused r    => BuildToolResult(id, $"Error: {r.Reason}", isError: true),
                TakeoverResult.Failed f     => BuildToolResult(id, $"Error: {f.Reason}", isError: true),
                TakeoverResult.Unauthorized => BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true),
                _                           => throw new InvalidOperationException(result.GetType().Name),
            };
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static bool OptionalBool(JsonObject? args, string name) =>
        args?[name] switch {
            null                                                  => false,
            JsonValue v when v.TryGetValue(out bool b)            => b,
            _                                                     => throw new ArgumentException($"'{name}' must be a boolean."),
        };

    internal static McpTool[] BuildToolsList() => [
        new(ToolName,
            "Continue another session's work: attach this session to its work items and unfinished plans, so "
          + "the app shows them here. Refuses while that session may still be running — a live agent process "
          + "on this machine, or a session active in the last hour that did not run here; ask the user before "
          + "retrying with force. Work items are skipped, not failed, on a plan without them. Returns what was "
          + "attached and skipped, and which plan is now current.",
            new("object", new() {
                ["session_id"] = new("string", "The session whose work this session takes over."),
                ["force"]      = new("boolean", "Take over even when that session looks live. Only after the user confirms.")
            }, ["session_id"]), McpToolAnnotations.Additive)
    ];

    // ... helpers copied from McpPlansServer ...
}
```

Before writing `McpToolArguments.RequireString`, confirm it exists (`grep -n "RequireString" src/Capacitor.Cli/Commands/McpToolArguments.cs`); it is used by the other servers.

- [ ] **Step 4: Register the server**

`KcapMcpServers.All`, after the `kcap-plans` entry:

```csharp
        new("kcap-handoff", ["mcp", "handoff"], NeedsProjectCwd: false,
            "Continue a session whose agent is gone: attach this session to its work items and unfinished plans.", AutoApprove: true),
```

`KcapMcpRegistry.Entries`:

```csharp
        ["kcap-handoff"]   = new("kcap-handoff",   ["mcp", "handoff"],   false),
```

`Program.cs`, in the `mcp` switch after `case "plans":`:

```csharp
            case "handoff":
                return await Run<McpHandoffServer>().RunAsync();
```

`CommandServices.cs`, after `services.AddTransient<McpPlansServer>();`:

```csharp
        services.AddTransient<McpHandoffServer>();
```

`kcap/.mcp.json`, after `kcap-plans` (no `cwd`, like `kcap-review`):

```json
    "kcap-handoff": {
      "command": "kcap",
      "args": ["mcp", "handoff"],
      "description": "Continue a session whose agent is gone — continue_session attaches this session to that session's work items and unfinished plans, refusing while it may still be running."
    },
```

- [ ] **Step 5: Docs**

`help-mcp.txt`: add `handoff` wherever the subcommands are listed at the top, and a section after `mcp sessions:`:

```
mcp handoff:
  Exposes one tool, continue_session, which attaches the current session to
  another session's work items and unfinished plans. Refuses while that
  session may still be running unless force is passed. Available on every
  plan. Requires `kcap login`.
```

Also fix the stale "Exposes four tools" line under `mcp sessions:` to name the eight tools listed in `kcap/README.md`.

`kcap/README.md`: a `### kcap-handoff` section after `kcap-sessions`, one-row table: `continue_session` | Attach this session to another session's work items and unfinished plans; refuses while that session may still be running.

`README.md`: find the MCP server list (`grep -n "kcap-plans" README.md`) and add `kcap-handoff` beside it with the same one-line description.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
Expected: all pass. `FlowsDriverSchemaConformanceTests` (both server lists agree), `McpCanonicalContractTests` and the bundled `.mcp.json` static-config test cover the registration. If a fixture pins the exact server list or count (grep the failing test for `kcap-knowledge`), add `kcap-handoff` to it — that is the intended surface change.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli/Commands/McpHandoffServer.cs src/Capacitor.Cli.Core/Mcp/KcapMcpServers.cs src/Capacitor.Cli.Core/KcapMcpRegistry.cs src/Capacitor.Cli/Program.cs src/Capacitor.Cli/Commands/CommandServices.cs kcap/.mcp.json src/Capacitor.Cli.Core/Resources/help-mcp.txt kcap/README.md README.md test/
git commit -m "Serve continue_session from a new kcap-handoff MCP server (#1235)" -m "Work items are not on every plan and kcap-sessions is auto-approved for reviewers, so neither can host it." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `kcap recap <X> --continue [--force]`

**Files:**
- Create: `src/Capacitor.Cli/Commands/RecapContinuation.cs`
- Modify: `src/Capacitor.Cli/ArgParsing.cs` (extract `PositionalSessionId`)
- Modify: `src/Capacitor.Cli/Program.cs` (`case "recap":`)
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs`
- Modify: `src/Capacitor.Cli.Core/Resources/help-recap.txt`, `README.md` (`kcap recap` section)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/RecapContinuationTests.cs`, `test/Capacitor.Cli.Tests.Unit/ArgParsingTests.cs` (create if absent; `grep -rln "ResolveSessionId" test/Capacitor.Cli.Tests.Unit` finds the existing home)

**Interfaces:**
- Consumes: `SessionTakeover`, `TakeoverResult`, `TakeoverReport` (Task 2).
- Produces: `static string? ArgParsing.PositionalSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null)`; `sealed class RecapContinuation(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http, TimeProvider time)` with `Task<int> RunAsync(string previous, bool force)` and `internal Task<int> RunWithAsync(HttpClient client, string baseUrl, string previous, string? current, bool force)`; exit codes `0` done, `1` failed, `2` refused (`RecapContinuation.Refused = 2`).

- [ ] **Step 1: Write the failing tests**

Arg parsing (bare `[NotInParallel]`: it sets an env var the parser would otherwise read):

```csharp
    [Test]
    [NotInParallel]
    public async Task PositionalSessionId_ignores_the_environment() {
        using var env = EnvScope.Exclusive("KCAP_SESSION_ID", "cccccccccccccccccccccccccccccccc");

        await Assert.That(ArgParsing.PositionalSessionId(["recap", "--continue"])).IsNull();
        await Assert.That(ArgParsing.PositionalSessionId(["recap", "--continue", "abc"])).IsEqualTo("abc");
        await Assert.That(ArgParsing.PositionalSessionId(["recap", "--get-turn", "3", "abc"], valueFlags: ["--get-turn"])).IsEqualTo("abc");
    }
```

`RecapContinuationTests` (console capture needs bare `[NotInParallel]`):

```csharp
using System.Net;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class RecapContinuationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Current  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    RecapContinuation Command() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);

    static HttpClient Serving(Func<string, (int, string)> byPath) => new(new Answers(byPath));

    [Test]
    public async Task Prints_the_continued_block_and_exits_zero() {
        using var output = ConsoleOutput.StartCapture();
        using var client = Serving(path => path.EndsWith("/summary") ? (200, """{"status":"ended"}""") : (200, "[]"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, Current, force: false);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(output.Text).Contains($"## Continued from session {Previous}");
    }

    [Test]
    public async Task A_refusal_exits_two_on_stderr() {
        using var error  = ConsoleOutput.StartErrorCapture();
        using var client = Serving(_ => (200, "{}"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, Previous, force: false);

        await Assert.That(code).IsEqualTo(RecapContinuation.Refused);
        await Assert.That(error.Text).Contains("cannot continue itself");
    }

    [Test]
    public async Task No_current_session_is_a_failure_naming_the_fix() {
        using var error  = ConsoleOutput.StartErrorCapture();
        using var client = Serving(_ => (200, "{}"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, current: null, force: false);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(error.Text).Contains("inside the session that takes over");
    }

    sealed class Answers(Func<string, (int Status, string Body)> byPath) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var (status, body) = byPath(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }
}
```

Check the capture helper's member names first (`grep -n "public" test/Capacitor.Tests.Helpers/ConsoleOutput*.cs`) and use its real text accessor in place of `.Text` if it differs.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RecapContinuationTests/*"`
Expected: build error — `RecapContinuation` / `PositionalSessionId` do not exist.

- [ ] **Step 3: Implement**

`ArgParsing.cs` — split the loop out of `ResolveSessionId`:

```csharp
    internal static string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) =>
        PositionalSessionId(args, skipCount, valueFlags) ?? ResolveSessionIdFromEnv();

    /// <summary>The positional session id alone, never the environment's: for a command where the
    /// ambient session is the wrong default.</summary>
    internal static string? PositionalSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) {
        // the existing loop body, returning `token` on the first positional and null at the end
    }
```

Program.cs has its own `ResolveSessionId` wrapper at line ~955; add a matching local `string? PositionalSessionId(string[] args, string[]? valueFlags = null) => ArgParsing.PositionalSessionId(args, valueFlags: valueFlags);` next to it.

`src/Capacitor.Cli/Commands/RecapContinuation.cs`:

```csharp
using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>The takeover half of <c>kcap recap &lt;id&gt; --continue</c>. A refusal has its own exit code
/// so the caller can withhold the recap: an agent that reads a recap carries on with the work.</summary>
sealed class RecapContinuation(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http, TimeProvider time) {
    public const int Refused = 2;

    public async Task<int> RunAsync(string previous, bool force) {
        var baseUrl = profiles.Resolution.ServerUrl;

        if (baseUrl is null || !HttpClientExtensions.IsAcceptableUrl(baseUrl)) {
            await Console.Error.WriteLineAsync(HttpClientExtensions.SchemeMissingHint);
            return 1;
        }

        using var client = await http.ForCommandAsync();

        return await RunWithAsync(client, baseUrl, previous, WorkContextIds.CanonicalSessionId(HarnessRequesterContext.Resolve().SessionId), force);
    }

    internal async Task<int> RunWithAsync(HttpClient client, string baseUrl, string previous, string? current, bool force) {
        if (current is null) {
            await Console.Error.WriteLineAsync(
                "kcap recap --continue must run inside the session that takes over (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).");
            return 1;
        }

        TakeoverResult result;

        try {
            result = await new SessionTakeover(AgentSessions.OnThisMachine(config), time).RunAsync(client, baseUrl, previous, current, force);
        } catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"Continuing session {previous} failed: {ex.Message}");
            return 1;
        }

        switch (result) {
            case TakeoverResult.Completed c:
                await Console.Out.WriteLineAsync(TakeoverReport.Render(c.Outcome));
                return c.AllWritesFailed ? 1 : 0;
            case TakeoverResult.Refused r:
                await Console.Error.WriteLineAsync(r.Reason);
                return Refused;
            case TakeoverResult.Failed f:
                await Console.Error.WriteLineAsync(f.Reason);
                return 1;
            default:
                await Console.Error.WriteLineAsync(await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time));
                return 1;
        }
    }
}
```

Check `HttpClientExtensions.SchemeMissingHint` and `IsAcceptableUrl` names against `McpPlansServer` (they are used there) and `AuthRejectionNotice.ForPersistentUnauthorizedAsync`'s return type (a string in `McpPlansServer.RelayAsync`).

`CommandServices.cs`: `services.AddTransient<RecapContinuation>();` next to `RecapCommand`'s registration (`grep -n "RecapCommand" src/Capacitor.Cli/Commands/CommandServices.cs`).

`Program.cs`, `case "recap":` — insert after the `useGetTurn` line and before `if (useRepo)`:

```csharp
        var useContinue = args.Contains("--continue");
        var continued   = 0;

        if (useContinue) {
            var previous = PositionalSessionId(args, valueFlags: ["--get-turn"]);

            if (previous is null || useRepo) {
                Console.Error.WriteLine("Usage: kcap recap <sessionId> --continue [--force] [--chain] [--full] [--per-turn] [--get-turn <N>]");
                Console.Error.WriteLine("  --continue needs the id of the session to continue, and does not combine with --repo.");

                return 1;
            }

            continued = await Run<RecapContinuation>().RunAsync(previous, args.Contains("--force"));

            if (continued == RecapContinuation.Refused) return RecapContinuation.Refused;
        }
```

Then make each existing `return await Run<RecapCommand>()...` in the non-repo path go through one exit: e.g. `var recapCode = usePerTurn ? await ...HandlePerTurnRecap(...) : useGetTurn ? ... : await ...HandleRecap(...);` then `return recapCode != 0 ? recapCode : continued;`. Keep the `--get-turn` value validation (return 1) ahead of that.

Add `--continue` and `--force` to the usage line printed when no session id is found.

`help-recap.txt`, under Options:

```
  --continue              Take over the session's work: attach this session to
                          its work items and unfinished plans, then print the
                          recap. Needs an explicit sessionId. Refuses (exit 2,
                          no recap) while that session may still be running
  --force                 With --continue: take over even when the session
                          looks live. Only after confirming it is not
```

`README.md` `kcap recap` section: add the same two flags and one example, `kcap recap 3f2a… --continue`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/RecapContinuationTests/*"`
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ArgParsingTests/*"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/RecapContinuation.cs src/Capacitor.Cli/ArgParsing.cs src/Capacitor.Cli/Program.cs src/Capacitor.Cli/Commands/CommandServices.cs src/Capacitor.Cli.Core/Resources/help-recap.txt README.md test/
git commit -m "Add kcap recap --continue to take over a session's work (#1235)" -m "A refusal prints no recap: an agent that reads one carries on as if the takeover happened." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `get_session_summary` lists work items

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpSessionsServer.cs` (`HandleSessionSummaryAsync`, `FetchDeclaredPlansAsync`, `ProjectRecapToSummary`, tool description in `BuildToolsList`)
- Modify: `kcap/README.md` (`get_session_summary` row)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`

**Interfaces:**
- Produces: `internal static string ProjectRecapToSummary(string body, string? plansBody = null, string? workItemsBody = null)`; `internal static string? ProjectWorkItems(string? body)`.

- [ ] **Step 1: Write the failing tests** — append to `McpSessionsServerTests`:

```csharp
    [Test]
    public async Task Summary_lists_the_sessions_work_items() {
        var json = McpSessionsServer.ProjectRecapToSummary("[]", null,
            """[{"work_item_id":"w1","label":"#1 — One","source":"declared","confidence":1.0,"is_primary":true}]""");

        var items = JsonNode.Parse(json)!["work_items"]!.AsArray();
        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0]!["work_item_id"]!.GetValue<string>()).IsEqualTo("w1");
        await Assert.That(items[0]!["label"]!.GetValue<string>()).IsEqualTo("#1 — One");
        await Assert.That(items[0]!["is_primary"]!.GetValue<bool>()).IsTrue();
        await Assert.That(items[0]!.AsObject().ContainsKey("confidence")).IsFalse();
    }

    [Test]
    public async Task Summary_omits_work_items_when_there_are_none_or_they_are_unavailable() {
        await Assert.That(JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary("[]", null, "[]"))!.AsObject().ContainsKey("work_items")).IsFalse();
        await Assert.That(JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary("[]", null, null))!.AsObject().ContainsKey("work_items")).IsFalse();
        await Assert.That(JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary("[]", null, """{"code":"work_items_not_in_plan"}"""))!.AsObject().ContainsKey("work_items")).IsFalse();
    }

    [Test]
    public async Task Summary_description_points_to_the_takeover() {
        var tool = McpSessionsServer.BuildToolsList().Single(t => t.Name == "get_session_summary");
        await Assert.That(tool.Description).Contains("continue_session");
        await Assert.That(tool.Description).Contains("kcap recap");
    }
```

Also find the existing dispatch test for `get_session_summary` (`grep -n "get_session_summary" test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`) and add one that routes `/api/work-items/session/{id}` to a 403 and asserts the summary still returns without `work_items` and without `isError`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: build error — no three-argument `ProjectRecapToSummary`.

- [ ] **Step 3: Implement**

- Rename `FetchDeclaredPlansAsync(client, url, ct)` to `FetchBestEffortAsync(HttpClient client, string url, string what, CancellationToken ct)`; its stderr line becomes `$"kcap mcp sessions: {what} lookup failed (...); returning the summary without them."`. A non-success status (the 403 included) returns null as today.
- In `HandleSessionSummaryAsync`, start a third lookup alongside the plans one, sharing `plansCts` (rename it `lookupsCts`): `var itemsTask = FetchBestEffortAsync(client, $"{baseUrl}/api/work-items/session/{Uri.EscapeDataString(sessionId)}", "work items", lookupsCts.Token);`. Pass `await itemsTask` as the third argument to `ProjectRecapToSummary`, and in the `finally` await it after `plansTask`.
- `ProjectRecapToSummary(string body, string? plansBody = null, string? workItemsBody = null)`: after the `declared_plans` append, add

```csharp
        if (ProjectWorkItems(workItemsBody) is { } workItems) {
            sb.Append(",\"work_items\":");
            sb.Append(workItems);
        }
```

- New method beside `ProjectDeclaredPlans`:

```csharp
    /// <summary>The session's work items as JSON text, or null when there are none or they could not be read.</summary>
    internal static string? ProjectWorkItems(string? body) {
        if (body is null) return null;

        try {
            if (JsonNode.Parse(body) is not JsonArray items) return null;

            var sb    = new StringBuilder("[");
            var count = 0;

            foreach (var item in items) {
                if (item?["work_item_id"] is not JsonValue idValue || !idValue.TryGetValue(out string? workItemId) || workItemId is null) continue;

                var label = item["label"] is JsonValue l && l.TryGetValue(out string? text) && text is not null ? text : workItemId;

                if (count++ > 0) sb.Append(',');

                sb.Append("{\"work_item_id\":");
                AppendJsonString(sb, workItemId);
                sb.Append(",\"label\":");
                AppendJsonString(sb, label);
                sb.Append(",\"is_primary\":").Append(item["is_primary"] is JsonValue p && p.TryGetValue(out bool primary) && primary ? "true" : "false");
                sb.Append('}');
            }

            return count == 0 ? null : sb.Append(']').ToString();
        } catch {
            return null;
        }
    }
```

- `get_session_summary` description (in `BuildToolsList`): append "Also lists `work_items` the session is attached to. To take over a session's work items and unfinished plans — continuing a session whose agent is gone — call `continue_session` in kcap-handoff, or run `kcap recap <id> --continue`."
- `kcap/README.md` row: `Concise summary_text + plan + declared_plans + work_items for a session`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/McpSessionsServer.cs kcap/README.md test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs
git commit -m "List a session's work items in get_session_summary (#1235)" -m "Best-effort like declared_plans: a plan without work items simply omits the field." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Skills

**Precondition:** PR #1236 rewrites the same skill sections. Run `gh pr view 1236 --json state`. If `MERGED`, `git fetch origin main && git rebase origin/main` first. If still open, ask the user whether to fold #1236's text into this branch; do not edit around it.

**Files:**
- Modify: `kcap/skills/recap/SKILL.md`, `kcap/skills/plans/SKILL.md`, `kcap/skills/work-items/SKILL.md`

- [ ] **Step 1: Recap skill** — replace the body of "Continuing another session's work" (the three numbered steps from #1236) with:

```markdown
When the user asks you to recap a session **and carry on with it** ("recap session X and continue", "pick up where X left off", "resume the killed agent"), this session takes over that session's work items and plans. Run:

```bash
kcap recap <X> --continue
```

It attaches this session to X's work items and unfinished plans, prints what it attached, then prints the recap. `continue_session(session_id: X)` in the `kcap-handoff` MCP server does the same takeover without the recap.

- **Exit 2 means it refused**: X may still be running — a live agent process on this machine, or a session active in the last hour that did not run here. Tell the user and ask. Retry with `--force` (`force: true`) only when they confirm X is gone. Never pass it on your own judgement.
- **Before resuming a plan task, verify it** as the `plans` skill's "Verify before continuing" says: a task left `in_progress` may be half-written.
- Tell the user which work items and which plan task you took over. Work items reported as not available on this plan are expected on the Free plan.
```

Keep the existing line in "Tips" but point it at `--continue` instead of the manual steps.

- [ ] **Step 2: Plans skill** — in "Resuming a plan", add before step 1: "When you are continuing another session (the user named one), use `kcap recap <X> --continue` instead — see the `recap` skill. The steps below are for a plan found through `list_repo_plans` with no session to continue." Leave steps 1–5 as they are.

- [ ] **Step 3: Work-items skill** — next to the `get_session_work_items` guidance, add one line: "Continuing another session's work? `kcap recap <X> --continue` attaches this session to its work items (and plans) in one step."

- [ ] **Step 4: Commit**

```bash
git add kcap/skills
git commit -m "Point the recap and plans skills at recap --continue (#1235)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Whole-branch verification

- [ ] **Step 1:** `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` — no warnings introduced.
- [ ] **Step 2:** `dotnet test --solution Capacitor.slnx` — all green. Known flakes are listed in memory; rerun an unrelated failure alone before debugging it.
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — no output.
- [ ] **Step 4:** `bash scripts/check-linear-ids.sh` — passes.
- [ ] **Step 5:** Smoke test in a real session: run `kcap recap <an ended session of yours> --continue` from inside a Claude Code session and confirm the `## Continued` block, then `kcap agent ls`/app shows the plan on the new session.
- [ ] **Step 6:** Comment on #1235 answering the open question: no continuation link is recorded, because `previous_session_id` is written only on SessionStart and means "followed in the same terminal", not shared work; the takeover attaches the shared work directly. File the follow-up issue: "Post SessionEnd for a session whose local agent process is gone" (the exit record could drive it; blocked on knowing which profile/server recorded the session).
