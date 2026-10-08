namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Claude;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

/// <summary>The hosted Claude permission seam against a stub judge: when it is consulted, what the
/// request declares for a hosted run, and what the decision event records of the consultation.</summary>
public class ClaudeHostedPolicySeamJudgeTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Sid     = "6ba7b8109dad11d180b400c04fd430c8";
    const string AgentId = "agent-1";

    const string Rejection = "The user doesn't want to proceed with this tool use. The tool use was rejected.";

    /// <summary>Far above the seam's own: under a loaded suite the transcript read can eat the
    /// production budget before the stub is ever asked.</summary>
    static readonly TimeSpan Ample = TimeSpan.FromSeconds(30);

    static PolicySnapshot Snapshot(string yaml) => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", yaml,
            PolicyDocumentBinder.Bind(yaml, PolicyScope.Repo))], false, []);

    static PolicySnapshot JudgeOn => Snapshot("version: 1\njudge:\n  mode: unmatched\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");

    PolicyJudgeGateway Gateway => new(
        () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), _server.Url, TimeProvider.System);

    /// <summary>One human message, then a call the human refused at a prompt.</summary>
    string Transcript() => Tmp.CreateFile("transcript.jsonl", string.Join("\n",
        """{"type":"user","uuid":"11111111-1111-1111-1111-111111111111","promptId":"p1","message":{"role":"user","content":"tidy the branch"}}""",
        """{"type":"assistant","uuid":"a1","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_0","name":"Bash","input":{"command":"rm -rf build"}}]}}""",
        $$$"""{"type":"user","uuid":"r1","promptId":"p1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_0","is_error":true,"content":"{{{Rejection}}}"}]}}""")
        + "\n");

    ClaudeHostedPermissionCall Call(string command, string? transcript = null) => new(
        Sid, AgentId, "Bash", JsonDocument.Parse(new JsonObject { ["command"] = command }.ToJsonString()).RootElement.Clone(),
        "/wt", "toolu_1", transcript ?? Transcript());

    Task<ClaudeHostedPolicyResult?> Evaluate(string command, PolicySnapshot? snapshot = null, TimeSpan? budget = null) =>
        ClaudeHostedPolicySeam.EvaluateAsync(Call(command), snapshot ?? JudgeOn, TimeProvider.System, Gateway, Config.Root, budget ?? Ample);

    void Judge(string outcome, int status = 200, int delayMs = 0) {
        var response = Response.Create().WithStatusCode(status).WithBody($$"""
            {"outcome":"{{outcome}}","model_outcome":"{{outcome}}","rationale":"the user asked for it",
             "user_authorization":"high","clamped":null,"lineage":"established","cache":"miss","failure_class":null,
             "consultation_id":"c-1","artifact_id":"a-1"}
            """);
        if (delayMs > 0) response = response.WithDelay(delayMs);
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(response);
    }

    List<JsonNode> JudgeRequests() => [.. _server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)];

    [Test]
    [Arguments("allow", PolicyOutcome.Allow, "allow", "allow")]
    [Arguments("deny", PolicyOutcome.Deny, "deny", "deny")]
    [Arguments("ask", PolicyOutcome.Ask, "ask", "parked")]
    public async Task An_unmatched_call_takes_the_judges_answer_and_records_its_consultation(
            string answer, PolicyOutcome outcome, string requested, string effective) {
        Judge(answer);

        var result = await Evaluate("git push");

        await Assert.That(result!.Outcome).IsEqualTo(outcome);
        await Assert.That(result.Event.Seam).IsEqualTo(PolicySeams.HostedClaudePermission);
        await Assert.That(result.Event.AgentId).IsEqualTo(AgentId);
        await Assert.That(result.Event.RequestedOutcome).IsEqualTo(requested);
        await Assert.That(result.Event.EffectiveOutcome).IsEqualTo(effective);
        await Assert.That(result.Event.MatchedRules).IsEmpty();
        await Assert.That(result.Event.FailureClass).IsNull();
        await Assert.That(result.Event.Judge!.ConsultationId).IsEqualTo("c-1");
        await Assert.That(result.Event.Judge.ArtifactId).IsEqualTo("a-1");
    }

    /// <summary>The hosted lane names its run so the server can resolve the snapshot the
    /// orchestrator staged under it; nothing travels inline.</summary>
    [Test]
    public async Task The_request_names_the_hosted_run_and_declares_the_transcripts_turns_and_refusals() {
        Judge("ask");

        await Evaluate("git push");

        var request = JudgeRequests().Single();
        await Assert.That(request["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(request["agent_id"]!.GetValue<string>()).IsEqualTo(AgentId);
        await Assert.That(request["seam"]!.GetValue<string>()).IsEqualTo("hosted_claude_permission");
        await Assert.That(request["snapshot_id"]!.GetValue<string>()).IsEqualTo("snap-1");
        await Assert.That(request["snapshot"]).IsNull();
        await Assert.That(request["turns"]!["user_messages"]![0]!["id"]!.GetValue<string>())
            .IsEqualTo("11111111-1111-1111-1111-111111111111");
        await Assert.That(request["turns"]!["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_1");
        await Assert.That(request["refusals"]!["complete"]!.GetValue<bool>()).IsTrue();
        await Assert.That(request["refusals"]!["source"]!.GetValue<string>()).IsEqualTo("transcript");
        await Assert.That(request["refusals"]!["entries"]![0]!["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_0");
        await Assert.That(request["refusals"]!["entries"]![0]!["target"]!.GetValue<string>()).IsEqualTo("rm -rf build");
    }

    /// <summary>An older CLI posts no transcript path. The judge still runs, but told the refusal
    /// history is unknown, so it cannot allow.</summary>
    [Test]
    public async Task A_request_with_no_transcript_declares_its_refusals_unknown() {
        Judge("ask");

        await ClaudeHostedPolicySeam.EvaluateAsync(
            Call("git push") with { TranscriptPath = null }, JudgeOn, TimeProvider.System, Gateway, Config.Root, Ample);

        var request = JudgeRequests().Single();
        await Assert.That(request["turns"]).IsNull();
        await Assert.That(request["refusals"]!["complete"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task The_default_budget_is_the_servers_full_ceiling() {
        Judge("ask");

        await ClaudeHostedPolicySeam.EvaluateAsync(Call("git push"), JudgeOn, TimeProvider.System, Gateway, Config.Root);

        var budget = JudgeRequests().Single()["budget_ms"]!.GetValue<int>();
        await Assert.That(budget).IsGreaterThan(4_000).And.IsLessThanOrEqualTo(5_000);
    }

    [Test]
    public async Task Uncertain_passes_through_and_is_recorded_with_why() {
        Judge("uncertain");

        var result = await Evaluate("git push");

        await Assert.That(result!.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.Event.RequestedOutcome).IsEqualTo("pass_through");
        await Assert.That(result.Event.EffectiveOutcome).IsEqualTo("pass_through");
        await Assert.That(result.Event.FailureClass).IsEqualTo(PolicyJudgeResult.Uncertain);
    }

    /// <summary>A gated server answers 405 and one predating the route 404, with no coded body.</summary>
    [Test]
    public async Task A_server_without_the_route_passes_through() {
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(405));

        var result = await Evaluate("git push");

        await Assert.That(result!.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.Event.FailureClass).IsEqualTo(PolicyJudgeResult.HttpError);
    }

    /// <summary>The timeout class is only ever the client's own deadline firing, so it proves the
    /// seam stopped waiting before the stub's answer arrived.</summary>
    [Test]
    public async Task A_judge_slower_than_the_budget_passes_through_inside_it() {
        Judge("deny", delayMs: 10_000);

        var result = await Evaluate("git push", budget: TimeSpan.FromMilliseconds(400));

        await Assert.That(result!.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.Event.FailureClass).IsEqualTo(PolicyJudgeResult.Timeout);
    }

    [Test]
    public async Task An_unreachable_server_passes_through() {
        var unreachable = new PolicyJudgeGateway(
            () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), "http://127.0.0.1:1", TimeProvider.System);

        var result = await ClaudeHostedPolicySeam.EvaluateAsync(
            Call("git push"), JudgeOn, TimeProvider.System, unreachable, Config.Root, Ample);

        await Assert.That(result!.Outcome).IsEqualTo(PolicyOutcome.None);
        await Assert.That(result.Event.FailureClass).IsEqualTo(PolicyJudgeResult.TransportError);
    }

    [Test]
    public async Task A_rule_decision_never_consults_the_judge() {
        Judge("deny");

        var result = await Evaluate("git status");

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(result!.Outcome).IsEqualTo(PolicyOutcome.Allow);
        await Assert.That(result.Event.Judge).IsNull();
    }

    [Test]
    public async Task A_policy_that_does_not_enable_the_judge_never_consults_it() {
        Judge("deny");

        var result = await Evaluate("git push", Snapshot("version: 1\nrules: []\n"));

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(result).IsNull().Because("an undecided call nothing consulted records nothing");
    }

    [Test]
    public async Task No_gateway_leaves_an_undecided_call_unrecorded() {
        var result = await ClaudeHostedPolicySeam.EvaluateAsync(Call("git push"), JudgeOn, TimeProvider.System);

        await Assert.That(result).IsNull();
    }
}
