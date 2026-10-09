using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;
using TUnit.Assertions.Enums;

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

    static string Task(string id, int ordinal, string status, string source = "mcp", string? note = null, bool partial = false) =>
        $$"""{"task_id":"{{id}}","ordinal":{{ordinal}},"title":"T{{ordinal}}","status":"{{status}}","source":"{{source}}","note":{{(note is null ? "null" : $"\"{note}\"")}}{{(partial ? ",\"status_partial\":true" : "")}}}""";

    static Routes Server(string summary, string? items = "[]", string? plans = "[]") {
        var routes = new Routes();
        routes.Get($"/api/sessions/{Previous}/summary", 200, summary);
        if (items is not null) routes.Get($"/api/work-items/session/{Previous}", 200, items);
        if (plans is not null) routes.Get($"/api/sessions/{Previous}/plans", 200, plans);
        routes.Post("/api/loose-ends/adopt", 200, """{"results":[]}""");
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

    [Test]
    public async Task Refuses_to_continue_itself_across_id_forms() {
        var routes = new Routes();
        var r = await Run(routes, previous: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", current: Previous);
        await Assert.That(r).IsTypeOf<TakeoverResult.Refused>();
        await Assert.That(((TakeoverResult.Refused)r).Reason).Contains("cannot continue itself");
        await Assert.That(routes.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Refuses_an_upper_case_dashed_guid_of_the_current_session() {
        var routes = new Routes();
        var r = await Run(routes, previous: "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB");
        await Assert.That(((TakeoverResult.Refused)r).Reason).Contains("cannot continue itself");
        await Assert.That(routes.Requests.Count).IsEqualTo(0);
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

    [Test]
    public async Task A_local_exit_record_proceeds_even_when_the_server_says_active() {
        DeadClaim(Previous);
        var r = await Run(Server(Active(TimeSpan.FromMinutes(1))));
        await Assert.That(Outcome(r)["liveness"]!.GetValue<string>()).IsEqualTo("exited");
    }

    [Test]
    public async Task An_upper_case_id_finds_the_local_exit_record() {
        DeadClaim(Previous);
        var r = await Run(Server(Active(TimeSpan.FromMinutes(1))), previous: "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
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

    [Test]
    public async Task Attaches_every_work_item_to_the_current_session() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"#1 — One","is_primary":true},{"work_item_id":"w2","label":"Two","is_primary":false}]""");
        routes.Post("/api/work-items/declare", 200, "{}");

        var o = Outcome(await Run(routes));

        var declares = routes.Posts.Where(p => p.Path == "/api/work-items/declare").ToList();
        await Assert.That(declares.Count).IsEqualTo(2);
        await Assert.That(declares.All(d => d.Body!["session_id"]!.GetValue<string>() == Current)).IsTrue();
        await Assert.That(o["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("ok");
        await Assert.That(o["work_items"]!["items"]!.AsArray().Count).IsEqualTo(2);
    }

    /// <summary>The server makes the last declared item primary, so the previous primary must go last.</summary>
    [Test]
    public async Task The_previous_sessions_primary_work_item_is_declared_last() {
        var items = """[{"work_item_id":"w1","label":"One","is_primary":false},{"work_item_id":"w2","label":"Two","is_primary":true},{"work_item_id":"w3","label":"Three","is_primary":false}]""";
        var routes = Server(Ended(), items: items);
        routes.Post("/api/work-items/declare", 200, "{}");

        await Run(routes);

        var order = routes.Posts.Where(p => p.Path == "/api/work-items/declare").Select(p => p.Body!["work_item_id"]!.GetValue<string>()).ToList();
        await Assert.That(order).IsEquivalentTo(new[] { "w1", "w3", "w2" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Work_items_outside_the_plan_are_reported_and_plans_are_still_adopted() {
        var routes = Server(Ended(), items: null, plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "in_progress")}]")}]");
        routes.Get($"/api/work-items/session/{Previous}", 403, """{"code":"work_items_not_in_plan","message":"Work Items require the Team or Enterprise plan."}""");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = await Run(routes);

        await Assert.That(r).IsTypeOf<TakeoverResult.Completed>();
        await Assert.That(((TakeoverResult.Completed)r).Unsuccessful).IsFalse();
        await Assert.That(Outcome(r)["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("not_in_plan");
        await Assert.That(Outcome(r)["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
    }

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

        await Assert.That(routes.Posts.Single(p => p.Path.StartsWith("/api/plans/", StringComparison.Ordinal)).Path).IsEqualTo("/api/plans/p1/tasks/t3");
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
        await Assert.That(reasons["mine"]).IsEqualTo("not_adoptable");
    }

    /// <summary>A partial task's note is withheld, so adopting it would re-send null and erase the note.</summary>
    [Test]
    public async Task A_task_whose_status_is_partial_is_passed_over_for_a_later_pending_one() {
        var tasks = $"[{Task("t1", 1, "in_progress", partial: true)},{Task("t2", 2, "pending")}]";
        var routes = Server(Ended(), plans: $"[{Plan("p1", true, tasks)}]");
        routes.Post("/api/plans/p1/tasks/t2", 200, "{}");

        await Run(routes);

        await Assert.That(routes.Posts.Single(p => p.Path.StartsWith("/api/plans/", StringComparison.Ordinal)).Path).IsEqualTo("/api/plans/p1/tasks/t2");
    }

    [Test]
    public async Task A_plan_whose_open_tasks_are_all_partial_is_not_adoptable() {
        var tasks = $"[{Task("t1", 1, "in_progress", partial: true)},{Task("t2", 2, "pending", partial: true)}]";
        var routes = Server(Ended(), plans: $"[{Plan("p1", true, tasks)}]");

        var o = Outcome(await Run(routes));

        await Assert.That(routes.Posts.Count(p => p.Path.StartsWith("/api/plans/", StringComparison.Ordinal))).IsEqualTo(0);
        await Assert.That(o["skipped_plans"]!.AsArray()[0]!["reason"]!.GetValue<string>()).IsEqualTo("not_adoptable");
    }

    [Test]
    public async Task A_full_page_of_plans_is_flagged_as_truncated() {
        var plans = "[" + string.Join(",", Enumerable.Range(0, SessionTakeover.PlansReadCap)
            .Select(i => Plan($"p{i}", false, $"[{Task("a", 1, "completed")}]", finished: true))) + "]";

        var o = Outcome(await Run(Server(Ended(), plans: plans)));

        await Assert.That(o["plans_truncated"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Fewer_plans_than_the_cap_are_not_flagged() {
        var o = Outcome(await Run(Server(Ended(), plans: $"[{Plan("p1", false, $"[{Task("a", 1, "completed")}]", finished: true)}]")));

        await Assert.That(o["plans_truncated"]).IsNull();
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

        await Assert.That(routes.Posts.Last(p => p.Path.StartsWith("/api/plans/", StringComparison.Ordinal)).Path).IsEqualTo("/api/plans/cur/tasks/a");
        await Assert.That(o["current_plan_id"]!.GetValue<string>()).IsEqualTo("cur");
    }

    [Test]
    public async Task A_dashed_guid_reaches_the_server_in_canonical_form() {
        var routes = Server(Ended());

        var r = await Run(routes, previous: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await Assert.That(routes.Requests).Contains($"/api/sessions/{Previous}/summary");
        await Assert.That(Outcome(r)["continued_from"]!.GetValue<string>()).IsEqualTo(Previous);
    }

    [Test]
    public async Task An_empty_note_is_resent() {
        var routes = Server(Ended(), plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending", note: "")}]")}]");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        await Run(routes);

        await Assert.That(routes.Posts.Single(p => p.Path == "/api/plans/p1/tasks/t1").Body!["note"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task A_summary_read_that_throws_is_a_failure() {
        var routes = new Routes();
        routes.Throw($"/api/sessions/{Previous}/summary");

        var r = await Run(routes);

        await Assert.That(r).IsTypeOf<TakeoverResult.Failed>();
    }

    [Test]
    public async Task A_work_items_read_that_throws_is_reported_and_plans_are_still_adopted() {
        var routes = Server(Ended(), items: null, plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Throw($"/api/work-items/session/{Previous}");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Outcome["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("failed");
        await Assert.That(r.Outcome["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(r.Unsuccessful).IsTrue();
    }

    [Test]
    public async Task A_work_items_body_that_is_not_an_array_is_a_failed_read() {
        var r = (TakeoverResult.Completed)await Run(Server(Ended(), items: """{"oops":true}"""));

        await Assert.That(r.Outcome["work_items"]!["status"]!.GetValue<string>()).IsEqualTo("failed");
        await Assert.That(r.Outcome["work_items"]!["error"]!.GetValue<string>()).Contains("malformed");
        await Assert.That(r.Unsuccessful).IsTrue();
    }

    [Test]
    public async Task A_plans_body_that_is_not_an_array_is_a_failed_read() {
        var r = (TakeoverResult.Completed)await Run(Server(Ended(), plans: "<html>"));

        await Assert.That(r.Outcome["plans_error"]!.GetValue<string>()).Contains("malformed");
        await Assert.That(r.Unsuccessful).IsTrue();
    }

    [Test]
    [Arguments("items")]
    [Arguments("plans")]
    public async Task A_401_on_a_follow_up_read_is_unauthorized_before_any_write(string which) {
        var routes = Server(Ended(),
            items: which == "items" ? null : """[{"work_item_id":"w1","label":"One"}]""",
            plans: which == "plans" ? null : $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Get(which == "items" ? $"/api/work-items/session/{Previous}" : $"/api/sessions/{Previous}/plans", 401, "");
        routes.Post("/api/work-items/declare", 200, "{}");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = await Run(routes);

        await Assert.That(r).IsTypeOf<TakeoverResult.Unauthorized>();
        await Assert.That(routes.Posts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_plans_read_that_throws_is_reported_and_work_items_are_still_attached() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: null);
        routes.Throw($"/api/sessions/{Previous}/plans");
        routes.Post("/api/work-items/declare", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Unsuccessful).IsTrue();
        await Assert.That(r.Outcome["plans_error"]!.GetValue<string>()).IsNotEmpty();
        await Assert.That(r.Outcome["work_items"]!["items"]!.AsArray()[0]!["attached"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task A_timed_out_write_is_reported_and_the_rest_continue() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Timeout("/api/work-items/declare");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Outcome["work_items"]!["items"]!.AsArray()[0]!["attached"]!.GetValue<bool>()).IsFalse();
        await Assert.That(r.Outcome["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
    }

    [Test]
    public async Task One_failed_write_fails_the_takeover_and_is_reported_on_its_entry() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Post("/api/work-items/declare", 500, "boom");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Unsuccessful).IsTrue();
        await Assert.That(r.Outcome["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
        var item = r.Outcome["work_items"]!["items"]!.AsArray()[0]!;
        await Assert.That(item["attached"]!.GetValue<bool>()).IsFalse();
        await Assert.That(item["error"]!.GetValue<string>()).Contains("500");
        await Assert.That(r.CredentialRejected).IsFalse();
        await Assert.That(r.Outcome.ContainsKey("unauthorized")).IsFalse();
    }

    [Test]
    public async Task A_401_on_a_write_keeps_the_partial_outcome_and_marks_it_unauthorized() {
        var routes = Server(Ended(), items: """[{"work_item_id":"w1","label":"One"}]""", plans: $"[{Plan("p1", true, $"[{Task("t1", 1, "pending")}]")}]");
        routes.Post("/api/work-items/declare", 401, "");
        routes.Post("/api/plans/p1/tasks/t1", 200, "{}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Unsuccessful).IsTrue();
        await Assert.That(r.CredentialRejected).IsTrue();
        await Assert.That(r.Outcome["unauthorized"]!.GetValue<bool>()).IsTrue();
        await Assert.That(r.Outcome["current_plan_id"]!.GetValue<string>()).IsEqualTo("p1");
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

        await Assert.That(r.Unsuccessful).IsTrue();
    }

    [Test]
    public async Task Claim_adoption_runs_even_when_work_items_are_not_in_the_plan() {
        var routes = Server(Ended(), items: null);
        routes.Get($"/api/work-items/session/{Previous}", 403, """{"code":"work_items_not_in_plan"}""");
        routes.Post("/api/loose-ends/adopt", 200, """{"results":[{"outcome":"transferred","attempted_claim_id":"old","claim":{"claim_id":"new","session_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]}""");
        var result = (TakeoverResult.Completed)await Run(routes);
        var adoption = routes.Posts.Single(p => p.Path == "/api/loose-ends/adopt");
        await Assert.That(adoption.Body!["previous_session_id"]!.GetValue<string>()).IsEqualTo(Previous);
        await Assert.That(adoption.Body["session_id"]!.GetValue<string>()).IsEqualTo(Current);
        await Assert.That(result.Outcome["loose_end_claims"]!["results"]![0]!["outcome"]!.GetValue<string>()).IsEqualTo("transferred");
        await Assert.That(result.Unsuccessful).IsFalse();
        await Assert.That(routes.Posts.Any(p => p.Path == "/api/work-items/declare")).IsFalse();
    }

    [Test]
    public async Task A_partial_claim_adoption_is_not_reported_as_a_successful_takeover() {
        var routes = Server(Ended());
        routes.Post("/api/loose-ends/adopt", 200, """{"results":[{"outcome":"transferred","attempted_claim_id":"old","claim":{"claim_id":"new"}},{"outcome":"ownership_lost","attempted_claim_id":"lost"}]}""");
        var result = (TakeoverResult.Completed)await Run(routes);
        await Assert.That(result.Unsuccessful).IsTrue();
        await Assert.That(result.Outcome["loose_end_claims"]!["results"]![1]!["attempted_claim_id"]!.GetValue<string>()).IsEqualTo("lost");
        await Assert.That(TakeoverReport.Render(result.Outcome)).Contains("ownership_lost");
        await Assert.That(TakeoverReport.Render(result.Outcome)).Contains("lost");
        await Assert.That(routes.Posts.Count(p => p.Path == "/api/loose-ends/adopt")).IsEqualTo(1);
    }

    [Test]
    [Arguments(404, "", "unsupported", true)]
    [Arguments(405, "", "unsupported", true)]
    [Arguments(404, "{\"code\":\"next_work_unavailable\"}", "unavailable", true)]
    [Arguments(404, "{\"code\":\"session_not_found\"}", "failed", true)]
    [Arguments(503, "{\"code\":\"loose_end_claims_unavailable\"}", "failed", true)]
    public async Task Claim_adoption_distinguishes_old_servers_from_failed_operations(int status, string body, string expected, bool unsuccessful) {
        var routes = Server(Ended());
        routes.Post("/api/loose-ends/adopt", status, body);
        var result = (TakeoverResult.Completed)await Run(routes);
        await Assert.That(result.Outcome["loose_end_claims"]!["status"]!.GetValue<string>()).IsEqualTo(expected);
        await Assert.That(result.Unsuccessful).IsEqualTo(unsuccessful);
        await Assert.That(routes.Posts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_recorded_adoption_waiting_for_projection_retains_its_identity() {
        var routes = Server(Ended());
        routes.Post("/api/loose-ends/adopt", 200, """{"results":[{"outcome":"recorded_catching_up","attempted_claim_id":"old","claim":{"claim_id":"new","session_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]}""");
        var result = (TakeoverResult.Completed)await Run(routes);
        await Assert.That(result.Unsuccessful).IsFalse();
        await Assert.That(TakeoverReport.Render(result.Outcome)).Contains("recorded_catching_up");
        await Assert.That(TakeoverReport.Render(result.Outcome)).Contains("new");
    }

    [Test]
    [Arguments("{\"claim_id\":\"new\",\"session_id\":\"other\"}", "\"old\"")]
    [Arguments("{\"claim_id\":\"new\"}", "\"old\"")]
    [Arguments("{\"claim_id\":\"new\",\"session_id\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}", "null")]
    [Arguments("{\"claim_id\":\"new\",\"session_id\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}", "\" \"")]
    public async Task Claim_adoption_requires_both_the_attempt_and_current_worker(string claim, string attempted) {
        var routes = Server(Ended());
        routes.Post("/api/loose-ends/adopt", 200,
            $$$"""{"results":[{"outcome":"transferred","attempted_claim_id":{{{attempted}}},"claim":{{{claim}}}}]}""");
        var result = (TakeoverResult.Completed)await Run(routes);
        await Assert.That(result.Unsuccessful).IsTrue();
        await Assert.That(result.Outcome["loose_end_claims"]!["status"]!.GetValue<string>()).IsEqualTo("partial");
        await Assert.That(routes.Posts.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("acquired")]
    [Arguments("already_owned")]
    [Arguments("transferred")]
    [Arguments("recorded_catching_up")]
    public async Task Claim_adoption_accepts_current_server_outcomes_and_canonical_worker_ids(string outcome) {
        var routes = Server(Ended());
        routes.Post("/api/loose-ends/adopt", 200,
            $$$"""{"results":[{"outcome":"{{{outcome}}}","attempted_claim_id":"old","claim":{"claim_id":"new","session_id":"BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"}}]}""");
        var result = (TakeoverResult.Completed)await Run(routes);
        await Assert.That(result.Unsuccessful).IsFalse();
        await Assert.That(result.Outcome["loose_end_claims"]!["status"]!.GetValue<string>()).IsEqualTo("ok");
    }

    [Test]
    public async Task Nothing_to_attach_is_a_success() {
        var r = (TakeoverResult.Completed)await Run(Server(Ended()));
        await Assert.That(r.Unsuccessful).IsFalse();
    }

    [Test]
    public async Task A_failed_work_items_read_with_nothing_attached_is_a_failure() {
        var routes = Server(Ended(), items: null);
        routes.Throw($"/api/work-items/session/{Previous}");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Unsuccessful).IsTrue();
    }

    [Test]
    public async Task A_failed_plans_read_with_nothing_attached_is_a_failure() {
        var routes = Server(Ended(), plans: null);
        routes.Get($"/api/sessions/{Previous}/plans", 500, "boom");

        var r = (TakeoverResult.Completed)await Run(routes);

        await Assert.That(r.Unsuccessful).IsTrue();
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
        readonly HashSet<string> _timingOut = [];

        public List<string> Requests { get; } = [];

        public List<(string Path, JsonObject? Body)> Posts { get; } = [];

        public void Get(string path, int status, string body)  => _routes[(HttpMethod.Get, path)]  = (status, body);
        public void Post(string path, int status, string body) => _routes[(HttpMethod.Post, path)] = (status, body);
        public void Throw(string path) => _throwing.Add(path);
        public void Timeout(string path) => _timingOut.Add(path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);

            if (request.Method == HttpMethod.Post) {
                var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                Posts.Add((path, text is null ? null : JsonNode.Parse(text) as JsonObject));
            }

            if (_timingOut.Contains(path)) throw new TaskCanceledException("timed out");
            if (_throwing.Contains(path)) throw new HttpRequestException("connection reset");

            return _routes.TryGetValue((request.Method, path), out var r)
                ? new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new StringContent(r.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        }
    }
}
