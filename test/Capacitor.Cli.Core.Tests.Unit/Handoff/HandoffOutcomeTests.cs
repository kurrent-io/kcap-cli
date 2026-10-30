using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.Handoff;

public class HandoffOutcomeTests {
    const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    static JsonObject Outcome(string json) => (JsonObject)JsonNode.Parse(json)!;

    static HandoffOutcomeDto Completed(string json, bool unsuccessful = false) =>
        HandoffOutcomeDto.From(Source, "Title", new TakeoverResult.Completed(Outcome(json), unsuccessful));

    static HandoffOutcomeItemDto Single(HandoffOutcomeDto dto, string kind) => dto.Items.Single(i => i.Kind == kind);

    [Test]
    public async Task Truncated_plans_add_an_unknown_item() {
        var dto = Completed("""{"work_items":{"status":"ok","items":[]},"plans":[],"skipped_plans":[],"plans_truncated":true,"loose_end_claims":{"status":"ok","results":[]}}""");

        var item = Single(dto, "plans");

        await Assert.That(item.Result).IsEqualTo("unknown");
        await Assert.That(dto.State).IsEqualTo("done");
    }

    [Test]
    public async Task A_not_adoptable_plan_is_skipped() {
        var dto = Completed("""{"work_items":{"status":"ok","items":[]},"plans":[],"skipped_plans":[{"plan_id":"p1","reason":"not_adoptable"}],"loose_end_claims":{"status":"ok","results":[]}}""");

        var item = Single(dto, "plan");

        await Assert.That(item.Result).IsEqualTo("skipped");
        await Assert.That(item.Detail).IsEqualTo("not_adoptable");
    }

    [Test]
    public async Task A_refused_claim_write_is_refused() {
        var dto = Completed("""{"work_items":{"status":"ok","items":[]},"plans":[],"skipped_plans":[],"loose_end_claims":{"status":"partial","results":[{"outcome":"ownership_lost"}]}}""", unsuccessful: true);

        await Assert.That(Single(dto, "claims").Result).IsEqualTo("refused");
    }

    [Test]
    public async Task A_failed_work_item_write_is_failed() {
        var dto = Completed("""{"work_items":{"status":"ok","items":[{"work_item_id":"w1","label":"#1 — One","attached":false,"error":"HTTP 500"}]},"plans":[],"skipped_plans":[],"loose_end_claims":{"status":"ok","results":[]}}""", unsuccessful: true);

        var item = Single(dto, "work_item");

        await Assert.That(item.Result).IsEqualTo("failed");
        await Assert.That(item.Label).IsEqualTo("#1 — One");
        await Assert.That(item.Detail).IsEqualTo("HTTP 500");
    }

    [Test]
    public async Task Unauthorized_maps_to_unauthorized() {
        var dto = HandoffOutcomeDto.From(Source, null, new TakeoverResult.Unauthorized());

        await Assert.That(dto.State).IsEqualTo("unauthorized");
        await Assert.That(dto.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_refusal_keeps_its_reason() {
        var dto = HandoffOutcomeDto.From(Source, null, new TakeoverResult.Refused("Session x is no longer ended."));

        await Assert.That(dto.State).IsEqualTo("refused");
        await Assert.That(dto.Detail).IsEqualTo("Session x is no longer ended.");
    }

    [Test]
    public async Task Interrupted_keeps_carried_items_and_marks_the_rest_unknown() {
        var outcome = Outcome("""
            {"work_items":{"status":"ok","items":[
                {"work_item_id":"w1","label":"One","attached":true},
                {"work_item_id":"w2","label":"Two","attached":null}]},
             "plans":[{"plan_id":"p1","task_id":"t1","title":"T1","status":"pending","attached":null}],
             "skipped_plans":[],
             "loose_end_claims":{"status":"unknown"}}
            """);

        var dto = HandoffOutcomeDto.From(Source, "Title", new TakeoverResult.Interrupted(outcome, "source_reservation_lost"));

        await Assert.That(dto.State).IsEqualTo("failed");
        await Assert.That(dto.Detail).IsEqualTo("source_reservation_lost");
        var results = dto.Items.Select(i => (i.Kind, i.Label, i.Result)).ToList();
        await Assert.That(results).IsEquivalentTo(new[] {
            ("work_item", "One", "carried"),
            ("work_item", "Two", "unknown"),
            ("plan", "T1", "unknown"),
            ("claims", "Loose-end claims", "unknown"),
        });
    }

    [Test]
    public async Task Running_and_server_refusal_carry_no_items() {
        var running = HandoffOutcomeDto.Running(Source, "Title");
        var refused = HandoffOutcomeDto.Refused(Source, "Title", "handoff_source_unavailable");

        await Assert.That(running.State).IsEqualTo("running");
        await Assert.That(refused.State).IsEqualTo("refused");
        await Assert.That(refused.Detail).IsEqualTo("handoff_source_unavailable");
        await Assert.That(running.Items.Count + refused.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_outcome_is_snake_case_on_the_wire() {
        var dto = HandoffOutcomeDto.Running(Source, "Title");

        var json = JsonSerializer.Serialize(dto, HandoffIpcJsonContext.Default.HandoffOutcomeDto);

        await Assert.That(json).Contains("\"source_session_id\":\"" + Source + "\"");
        await Assert.That(json).Contains("\"source_title\":\"Title\"");
    }
}
