using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class McpFlowsServerTests {
    static Func<TimeSpan, Task> NoDelay(List<TimeSpan> recorded) => ts => { recorded.Add(ts); return Task.CompletedTask; };

    [Test]
    public async Task start_review_flow_description_discloses_the_paid_hosted_reviewer() {
        // Contract: the paid-cost disclosure the Pi bridge (and every harness that shows the flows
        // tool descriptions) relies on must actually be present in the tools/list output.
        var startReviewFlow = McpFlowsServer.BuildToolsList().Single(t => t.Name == "start_review_flow");

        await Assert.That(startReviewFlow.Description).Contains("PAID");
        await Assert.That(startReviewFlow.Description.ToLowerInvariant()).Contains("hosted reviewer");
    }

    [Test]
    public async Task Roundless_start_renders_started_envelope() {
        var body = """{"flow_run_id":"f1","round_id":null,"round_number":null,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":null,"reviewer_session_id":null}""";

        var text = McpFlowsServer.TryFormatRoundlessStart(body, out var pendingIds);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("flow_run_id: f1");
        await Assert.That(text).Contains("status: running");
        await Assert.That(text).Contains("send_to_participant");
        await Assert.That(pendingIds).IsEmpty();
    }

    [Test]
    public async Task Roundless_start_renders_and_exposes_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_id":null,"round_number":null,"status":"running",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"early note","received_at":"2026-07-06T00:00:00Z"}
             ]}
            """;

        var text = McpFlowsServer.TryFormatRoundlessStart(body, out var pendingIds);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("pending_messages (1):");
        await Assert.That(text).Contains("- from tester [msg-a1]: early note");
        await Assert.That(pendingIds).IsEquivalentTo(new[] { "msg-a1" });
    }

    [Test]
    public async Task Single_participant_start_with_round_is_not_roundless() {
        var body = """{"flow_run_id":"f1","round_id":"r1","round_number":1,"status":"running"}""";
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart(body, out _)).IsNull();
    }

    [Test]
    public async Task Unparseable_body_is_not_roundless() {
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart("not json", out _)).IsNull();
    }

    [Test]
    public async Task Wrong_typed_pending_fields_render_empty_instead_of_throwing() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":123,"from_participant":{"x":1},"text":"still shown","received_at":"2026-07-06T00:00:00Z"},
                {"message_id":"msg-ok","from_participant":"tester","text":"fine","received_at":"2026-07-06T00:00:00Z"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        await Assert.That(text).Contains("- from  []: still shown");
        await Assert.That(text).Contains("- from tester [msg-ok]: fine");
        await Assert.That(pendingIds).IsEquivalentTo(new[] { "msg-ok" });
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
    public async Task Status_response_renders_vendor_audit_and_participants() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "requested_reviewer_vendor":"claude","applied_reviewer_vendor":"claude",
             "reviewer_vendor_source":"explicit",
             "participants":[{"role":"reviewer","vendor":"claude","model":"sonnet","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).Contains("requested_reviewer_vendor: claude");
        await Assert.That(text).Contains("applied_reviewer_vendor: claude");
        await Assert.That(text).Contains("reviewer_vendor_source: explicit");
        await Assert.That(text).Contains("reviewer: vendor=claude model=sonnet status=running");
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

    [Test]
    public async Task Malformed_pending_entry_is_skipped() {
        var body = """
            {"flow_run_id":"f1","round_number":2,"status":"closed","round_status":"clean","round_result_text":"all clean",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"first"},
                "junk-string",
                {"message_id":"msg-b2","from_participant":"reviewer","text":"second"}
             ]}
            """;
        var node = JsonNode.Parse(body)!.AsObject();

        // Pins the carried Minor from Task 2's review: a malformed (non-object) array entry must
        // be skipped, not throw — FormatPolledRoundResult has no try/catch, so a throw here would
        // turn a terminal result into a generic internal error.
        var text = McpFlowsServer.FormatPolledRoundResult(node, "f1", out var pendingIds);

        await Assert.That(text).Contains("from tester [msg-a1]: first");
        await Assert.That(text).Contains("from reviewer [msg-b2]: second");
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);

        // Pins the E-c final-review Minor: the header count must reflect the RENDERED entries (2 —
        // the two well-formed objects), not the raw array length (3, which also counts the
        // "junk-string" entry that gets skipped).
        await Assert.That(text).Contains("pending_messages (2):");
        await Assert.That(text).DoesNotContain("pending_messages (3):");
    }

    [Test]
    public async Task Ack_posts_rendered_ids_snake_case() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1", "m2"], NoDelay([]));

        await Assert.That(server.LogEntries.Count()).IsEqualTo(1);
        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"message_ids\"");
        var parsed = JsonNode.Parse(body)!.AsObject();
        var ids    = parsed["message_ids"]!.AsArray().Select(n => n!.GetValue<string>());
        await Assert.That(ids).IsEquivalentTo(["m1", "m2"]);
    }

    [Test]
    public async Task Ack_retries_once_then_swallows() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1"], NoDelay(delays));

        await Assert.That(server.LogEntries.Count()).IsEqualTo(2);
        await Assert.That(delays).HasCount().EqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Ack_attempt_times_out_and_is_swallowed() {
        // E-c final review, Important: the shared MCP client normally has
        // Timeout = InfiniteTimeSpan (the review-flow endpoints long-poll), so
        // AckRenderedMessagesAsync now bounds each POST attempt itself (PerAckPostTimeout, 15s).
        // Driving the real 15s bound here would make this test slow without adding coverage —
        // instead this pins the pre-existing swallow behavior the new bound feeds into: an
        // HttpClient with its OWN short Timeout produces the same OperationCanceledException
        // shape TryPostAsync's bare catch already swallows, deterministically and fast.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(300)));
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(50) };

        var delays = new List<TimeSpan>();
        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1"], NoDelay(delays));

        // No exception propagated, and the retry-after-delay path still ran once — i.e. both the
        // initial attempt and the retry timed out and were swallowed rather than thrown.
        await Assert.That(delays).HasCount().EqualTo(1);
    }

    [Test]
    public async Task Ack_skips_empty_ids() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", [], NoDelay([]));

        await Assert.That(server.LogEntries.Count()).IsEqualTo(0);
    }

    /// <summary>
    /// Ordering pin, composition-shaped: the real dispatch path (HandleToolCallAsync) isn't
    /// directly invocable from a unit test (it needs a live authenticated HttpClient and the full
    /// stdio JSON-RPC loop — that seam is covered by Capacitor.Cli.Tests.Integration instead). So
    /// this test pins the composition explicitly: format a status response through the
    /// id-exposing overload (the same call the get_flow_status wiring makes), then ack exactly
    /// the ids that call returned — and assert the ack body carries exactly those ids, matching
    /// what the wiring in McpFlowsServer's get_review_flow_status/get_flow_status arm does.
    /// </summary>
    [Test]
    public async Task Ordering_pin_format_then_ack_sends_exactly_the_rendered_ids() {
        var statusBody = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(statusBody, out var pendingIds);
        await Assert.That(text).Contains("pending_messages (2):");

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", pendingIds, NoDelay([]));

        await Assert.That(server.LogEntries.Count()).IsEqualTo(1);
        var ackBody = server.LogEntries.Single().RequestMessage.Body!;
        var ackIds  = JsonNode.Parse(ackBody)!.AsObject()["message_ids"]!.AsArray().Select(n => n!.GetValue<string>());
        await Assert.That(ackIds).IsEquivalentTo(pendingIds);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }
}
