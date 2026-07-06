using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class McpFlowsServerTests {
    [Test]
    public async Task Roundless_start_renders_started_envelope() {
        var body = """{"flow_run_id":"f1","round_id":null,"round_number":null,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":null,"reviewer_session_id":null}""";

        var text = McpFlowsServer.TryFormatRoundlessStart(body);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("flow_run_id: f1");
        await Assert.That(text).Contains("status: running");
        await Assert.That(text).Contains("send_to_participant");
    }

    [Test]
    public async Task Single_participant_start_with_round_is_not_roundless() {
        var body = """{"flow_run_id":"f1","round_id":"r1","round_number":1,"status":"running"}""";
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart(body)).IsNull();
    }

    [Test]
    public async Task Unparseable_body_is_not_roundless() {
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart("not json")).IsNull();
    }
}
