namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Policy;
using WireMock.ResponseBuilders;
using WireMock.Server;

/// <summary>The judge's wire contract as the client speaks it, and every way an answer becomes
/// pass-through.</summary>
public class PolicyJudgeClientTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    PolicyJudgeClient Client => new(new HttpClient(), _server.Url!, TimeProvider.System);

    static PolicyJudgeRequestV1 Request(int budgetMs) => new(
        "9dc2775376454e4691ecc2d69973c152", null, "claude", PolicySeams.ClaudePreToolUse, "snap", PolicyEngine.Version,
        new PolicyActionV1("shell", "claude", "git push", true, [["git", "push"]], null, null, null, null, null, null,
            "Bash", null, false, null),
        new PolicyJudgeTurnSetV1([new("11111111-1111-1111-1111-111111111111", "p1")], "toolu_1"),
        new PolicyJudgeRefusalsV1(true, PolicyJudgeRefusalsV1.SourceTranscript,
            [new("toolu_0", "Bash", "git push --force", "p1")]),
        null, budgetMs);

    void Answer(string body, int status = 200, int delayMs = 0) {
        var response = Response.Create().WithStatusCode(status).WithBody(body);
        if (delayMs > 0) response = response.WithDelay(delayMs);
        _server.Given(WireMock.RequestBuilders.Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(response);
    }

    static string Verdict(string outcome) => $$"""
        {"outcome":"{{outcome}}","model_outcome":"allow","rationale":"r","user_authorization":"low","clamped":"refusals_incomplete",
         "lineage":"none","cache":"hit","failure_class":null,"consultation_id":"c","artifact_id":"a",
         "classifier":{"base_prompt_version":"v","context_builder_version":"l"},"latency_ms":3}
        """;

    /// <summary>The server binds these names with its own contract types; a rename on either side
    /// fails the request as a 400, which reads as pass-through and hides the break.</summary>
    [Test]
    public async Task Sends_the_snake_case_contract_with_the_budget_less_transit() {
        Answer(Verdict("ask"));
        await Client.ConsultAsync(Request, TimeSpan.FromSeconds(2));

        var body = JsonNode.Parse(_server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("9dc2775376454e4691ecc2d69973c152");
        await Assert.That(body["engine_version"]!.GetValue<string>()).IsEqualTo(PolicyEngine.Version);
        await Assert.That(body["budget_ms"]!.GetValue<int>()).IsEqualTo(1750);
        await Assert.That(body["action"]!["raw_tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(body["turns"]!["user_messages"]![0]!["prompt_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(body["refusals"]!["entries"]![0]!["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_0");
        await Assert.That(body["refusals"]!["complete"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    [Arguments("allow", PolicyOutcome.Allow)]
    [Arguments("ask", PolicyOutcome.Ask)]
    [Arguments("deny", PolicyOutcome.Deny)]
    public async Task A_verdict_maps_to_its_outcome_with_provenance(string outcome, PolicyOutcome expected) {
        Answer(Verdict(outcome));
        var result = await Client.ConsultAsync(Request, TimeSpan.FromSeconds(2));

        await Assert.That(result.Outcome).IsEqualTo(expected);
        await Assert.That(result.FailureClass).IsNull();
        await Assert.That(result.Consultation!.ConsultationId).IsEqualTo("c");
        await Assert.That(result.Consultation.Clamped).IsEqualTo("refusals_incomplete");
        await Assert.That(result.Consultation.ModelOutcome).IsEqualTo("allow");
        await Assert.That(result.Rationale).IsEqualTo("r");
    }

    [Test]
    public async Task Uncertain_is_pass_through_carrying_the_servers_class() {
        Answer(Verdict("uncertain").Replace("\"failure_class\":null", "\"failure_class\":\"overloaded\""));
        var result = await Client.ConsultAsync(Request, TimeSpan.FromSeconds(2));

        await Assert.That(result.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.FailureClass).IsEqualTo(PolicyJudgeResult.Uncertain);
        await Assert.That(result.Consultation!.FailureClass).IsEqualTo("overloaded");
    }

    [Test]
    [Arguments(400)]
    [Arguments(401)]
    [Arguments(404)]
    [Arguments(405)]
    [Arguments(500)]
    public async Task Any_non_200_is_pass_through(int status) {
        Answer("""{"error":"policy_judge_request_invalid"}""", status);
        var result = await Client.ConsultAsync(Request, TimeSpan.FromSeconds(2));

        await Assert.That(result.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.FailureClass).IsEqualTo(PolicyJudgeResult.HttpError);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"outcome":"maybe"}""")]
    [Arguments("null")]
    public async Task An_unreadable_answer_is_pass_through(string body) {
        Answer(body);
        var result = await Client.ConsultAsync(Request, TimeSpan.FromSeconds(2));

        await Assert.That(result.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.FailureClass).IsEqualTo(PolicyJudgeResult.MalformedResponse);
    }

    [Test]
    public async Task A_slow_judge_is_abandoned_at_the_budget() {
        Answer(Verdict("deny"), delayMs: 5_000);
        var started = TimeProvider.System.GetTimestamp();
        var result = await Client.ConsultAsync(Request, TimeSpan.FromMilliseconds(300));

        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(3));
        await Assert.That(result.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.FailureClass).IsEqualTo(PolicyJudgeResult.Timeout);
    }

    /// <summary>Thrown by a handler, not provoked against a closed port: Windows retries a refused
    /// connection for about two seconds, which the budget reads as a timeout.</summary>
    [Test]
    public async Task A_transport_failure_is_pass_through() {
        using var http = new HttpClient(new FailingHandler());
        var result = await new PolicyJudgeClient(http, "http://judge.test", TimeProvider.System)
            .ConsultAsync(Request, TimeSpan.FromSeconds(2));

        await Assert.That(result.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.FailureClass).IsEqualTo(PolicyJudgeResult.TransportError);
    }

    sealed class FailingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
