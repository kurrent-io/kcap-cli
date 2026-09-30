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
