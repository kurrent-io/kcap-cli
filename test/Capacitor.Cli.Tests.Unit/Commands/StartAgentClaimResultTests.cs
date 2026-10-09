using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StartAgentClaimResultTests {
    [Test]
    [Arguments("pending", "unknown")]
    [Arguments("pending", "not_sent")]
    [Arguments("requested", "sent")]
    public async Task Launch_reporting_distinguishes_delivery_from_durable_reservation(string status, string dispatch) {
        var (text, error) = StartAgentTool.Render(200,
            $$$"""{"status":"{{{status}}}","agent_id":"agent","url":"https://example/agents/agent","loose_end_claim_id":"claim","dispatch_state":"{{{dispatch}}}","work_item":{"id":null,"reason":"loose_end"}}""", "le:end");
        await Assert.That(error).IsFalse();
        await Assert.That(text).Contains("status: " + status);
        await Assert.That(text).Contains("loose_end_claim_id: claim");
        await Assert.That(text).Contains("dispatch_state: " + dispatch);
        if (status == "pending") {
            await Assert.That(text).DoesNotContain("status: requested");
            await Assert.That(text).DoesNotContain("launch command was sent");
            await Assert.That(text).Contains("Do not start it again");
        }
    }

    [Test]
    public async Task Pending_without_a_claim_is_not_reported_as_a_successful_dispatch() {
        var (text, error) = StartAgentTool.Render(200, """{"status":"pending","agent_id":"agent","dispatch_state":"not_sent"}""", "le:end");
        await Assert.That(error).IsTrue();
        await Assert.That(text).DoesNotContain("status: requested");
        await Assert.That(text).DoesNotContain("most likely started");
    }
}
