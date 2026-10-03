using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class StartAgentRenderTests {
    const string Requested = """
        {"status":"requested","agent_id":"a1b2c3d4","url":"https://cap.test/agents/a1b2c3d4","daemon":"mac-studio",
         "repo_path":"/Users/x/dev/repo","vendor":"claude","model":"default",
         "work_item":{"id":"9d35573ceee554d58c1bbc909fe7d099","reason":"named"}}
        """;

    [Test]
    public async Task A_requested_start_lists_what_the_server_used() {
        var (text, isError) = StartAgentTool.Render(200, Requested);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).StartsWith("status: requested\n");
        await Assert.That(text).Contains("agent_id: a1b2c3d4\n");
        await Assert.That(text).Contains("url: https://cap.test/agents/a1b2c3d4\n");
        await Assert.That(text).Contains("daemon: mac-studio\n");
        await Assert.That(text).Contains("repo_path: /Users/x/dev/repo\n");
        await Assert.That(text).Contains("vendor: claude\n");
        await Assert.That(text).Contains("model: default\n");
        await Assert.That(text).EndsWith(StartAgentTool.RequestedNotice);
    }

    [Test]
    public async Task A_requested_start_promises_an_attempt_and_claims_no_attachment() {
        var (text, _) = StartAgentTool.Render(200, Requested);

        await Assert.That(text).Contains("work_item: 9d35573ceee554d58c1bbc909fe7d099 (named)");
        await Assert.That(text).Contains("will be attempted");
        await Assert.That(text).DoesNotContain("attached");
        await Assert.That(text).Contains("Do not wait for it and do not poll");
    }

    [Test]
    public async Task A_requested_start_with_no_work_item_gives_the_reason() {
        var (text, isError) = StartAgentTool.Render(200, """
            {"status":"requested","agent_id":"a1b2c3d4","url":"u","daemon":"d","repo_path":"/r","vendor":"claude","model":"default",
             "work_item":{"id":null,"reason":"requester_has_no_item"}}
            """);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("work_item: none (requester_has_no_item)\n");
        await Assert.That(text).DoesNotContain("will be attempted");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("[]")]
    public async Task An_accepted_start_with_an_unreadable_answer_says_an_agent_was_most_likely_started(string body) {
        var (text, isError) = StartAgentTool.Render(200, body);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(StartAgentTool.UnreadableAnswer);
    }

    /// <summary>Exact equality: a refusal is the server's message and nothing else. The 503 is here
    /// because nothing was sent, so nothing may hint that an agent is running.</summary>
    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(400, "unsupported_target")]
    [Arguments(403, "not_owner")]
    [Arguments(404, "session_not_found")]
    [Arguments(404, "work_item_not_visible")]
    [Arguments(409, "session_agent_mismatch")]
    [Arguments(409, "work_items_unavailable")]
    [Arguments(409, "next_work_unavailable")]
    [Arguments(409, "work_item_resolved")]
    [Arguments(409, "already_in_progress")]
    [Arguments(409, "no_daemon_on_this_machine")]
    [Arguments(409, "daemon_not_responding")]
    [Arguments(409, "daemon_changed")]
    [Arguments(503, "start_not_recorded")]
    public async Task A_coded_refusal_is_the_servers_message_and_nothing_else(int status, string code) {
        var (text, isError) = StartAgentTool.Render(status, $$"""{"error":"{{code}}","message":"The server's own words."}""");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo($"Error ({code}): The server's own words.");
    }

    [Test]
    public async Task A_refusal_shows_the_details_the_server_sent() {
        var ambiguous = StartAgentTool.Render(409, """{"error":"ambiguous_daemon","message":"Several daemons run here.","daemons":["mac-studio","mac-studio-2"]}""").Text;
        var vendor    = StartAgentTool.Render(409, """{"error":"vendor_unavailable","message":"m","vendors":["claude","codex"]}""").Text;
        var capacity  = StartAgentTool.Render(409, """{"error":"daemon_at_capacity","message":"m","active":4,"max":4}""").Text;
        var limit     = StartAgentTool.Render(409, """{"error":"start_limit_reached","message":"m","limit":15}""").Text;

        await Assert.That(ambiguous).IsEqualTo("Error (ambiguous_daemon): Several daemons run here.\ndaemons: mac-studio, mac-studio-2");
        await Assert.That(vendor).IsEqualTo("Error (vendor_unavailable): m\nvendors: claude, codex");
        await Assert.That(capacity).IsEqualTo("Error (daemon_at_capacity): m\nactive: 4\nmax: 4");
        await Assert.That(limit).IsEqualTo("Error (start_limit_reached): m\nlimit: 15");
    }

    [Test]
    public async Task A_code_without_a_message_is_still_a_refusal() {
        var (text, isError) = StartAgentTool.Render(409, """{"error":"daemon_changed"}""");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error (daemon_changed)");
    }

    /// <summary>The server's message for this code already says the outcome is unknown, so the
    /// tool adds nothing to it.</summary>
    [Test]
    public async Task A_send_that_threw_is_relayed_in_the_servers_words() {
        var (text, isError) = StartAgentTool.Render(502,
            """{"error":"failed","message":"The launch command could not be confirmed as sent. The agent may or may not have started."}""");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(
            "Error (failed): The launch command could not be confirmed as sent. The agent may or may not have started.");
    }

    [Test]
    public async Task A_record_that_was_not_written_is_relayed_as_nothing_sent() {
        var (text, isError) = StartAgentTool.Render(503,
            """{"error":"start_not_recorded","message":"The start could not be recorded, so nothing was sent. Try again."}""");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error (start_not_recorded): The start could not be recorded, so nothing was sent. Try again.");
    }

    [Test]
    public async Task An_uncoded_server_error_is_shown_as_sent_with_the_outcome_unknown() {
        var (text, isError) = StartAgentTool.Render(500, "upstream exploded");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: HTTP 500 — upstream exploded\n" + StartAgentTool.OutcomeUnknown);
    }

    [Test]
    public async Task An_uncoded_400_is_shown_as_the_server_sent_it() {
        var (text, isError) = StartAgentTool.Render(400, """{"title":"Bad Request","status":400}""");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("""Error: HTTP 400 — {"title":"Bad Request","status":400}""");
    }

    /// <summary>A Capacitor host answers an absent POST under /api with a bare 405; any other
    /// server in front of it answers 404.</summary>
    [Test]
    [Arguments(405, "")]
    [Arguments(404, "")]
    [Arguments(404, "{}")]
    [Arguments(404, "Not Found")]
    [Arguments(404, """{"title":"Not Found","status":404}""")]
    public async Task An_uncoded_404_or_405_means_the_server_cannot_start_agents(int status, string body) {
        var (text, isError) = StartAgentTool.Render(status, body);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(StartAgentTool.ServerCannotStartAgents);
    }

    [Test]
    public async Task A_404_with_a_code_is_a_refusal_and_is_relayed() {
        var (text, _) = StartAgentTool.Render(404, """{"error":"session_not_found","message":"No such session."}""");

        await Assert.That(text).IsEqualTo("Error (session_not_found): No such session.");
        await Assert.That(text).DoesNotContain("cannot start agents");
    }
}
