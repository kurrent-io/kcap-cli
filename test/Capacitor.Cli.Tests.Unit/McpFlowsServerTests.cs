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

    [Test]
    public async Task Status_response_renders_pending_messages_and_returns_ids() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/","received_at":"2026-07-06T00:00:00Z"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched","received_at":"2026-07-06T00:00:01Z"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var firstIndex  = text.IndexOf("from tester [msg-a1]: found a broken symlink in scripts/", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]: heads-up, migration file also touched", StringComparison.Ordinal);
        await Assert.That(firstIndex).IsGreaterThan(-1);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);

        // The existing (id-discarding) overload still works and renders the same text.
        var thinWrapperText = McpFlowsServer.FormatStatusResponse(body);
        await Assert.That(thinWrapperText).IsEqualTo(text);
    }

    [Test]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t"}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t","pending_messages":null}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t","pending_messages":[]}""")]
    public async Task Status_response_without_pending_renders_nothing(string body) {
        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).DoesNotContain("pending_messages");
        await Assert.That(pendingIds).IsEmpty();
    }

    [Test]
    public async Task Round_response_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_id":"r1","status":"findings","result_kind":"findings","result_text":"some findings",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;

        var text = McpFlowsServer.FormatRoundResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var resultIndex  = text.IndexOf("some findings", StringComparison.Ordinal);
        var pendingIndex = text.IndexOf("pending_messages (2):", StringComparison.Ordinal);
        await Assert.That(pendingIndex).IsGreaterThan(resultIndex);
        var firstIndex  = text.IndexOf("from tester [msg-a1]:", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]:", StringComparison.Ordinal);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }

    [Test]
    public async Task Close_response_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","status":"closed",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"}
             ]}
            """;

        var text = McpFlowsServer.FormatCloseResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (1):");
        await Assert.That(text).Contains("- from tester [msg-a1]: found a broken symlink in scripts/");
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1"]);
    }

    [Test]
    public async Task Polled_round_result_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_number":2,"status":"closed","round_status":"clean","round_result_text":"all clean",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();

        var text = McpFlowsServer.FormatPolledRoundResult(node, "f1", out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var resultIndex  = text.IndexOf("all clean", StringComparison.Ordinal);
        var pendingIndex = text.IndexOf("pending_messages (2):", StringComparison.Ordinal);
        await Assert.That(pendingIndex).IsGreaterThan(resultIndex);
        var firstIndex  = text.IndexOf("from tester [msg-a1]:", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]:", StringComparison.Ordinal);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }
}
