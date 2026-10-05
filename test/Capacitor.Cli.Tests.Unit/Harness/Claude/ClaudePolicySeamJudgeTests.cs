namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Policy;
using Capacitor.Cli.Tests.Unit.Policy;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

/// <summary>The local Claude seams against a stub judge: when it is consulted, what its answer does
/// to the vendor's decision, and what the decision event records of the consultation.</summary>
public class ClaudePolicySeamJudgeTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Sid = "9dc2775376454e4691ecc2d69973c152";
    const string JudgeOn = "version: 1\njudge:\n  mode: unmatched\n";

    /// <summary>Far above the hook's own: under a loaded suite the first transcript read can eat
    /// the production budget before the stub is ever asked.</summary>
    static readonly TimeSpan Ample = TimeSpan.FromSeconds(30);

    ClaudePolicySeam Seam => new(Config.Root, TimeProvider.System, new PolicyJudgeGateway(
        () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)),
        _server.Url, TimeProvider.System));

    void WriteUserPolicy(string yaml) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    string Transcript() {
        var path = Tmp.PathTo("transcript.jsonl");
        File.WriteAllText(path,
            """{"type":"user","uuid":"11111111-1111-1111-1111-111111111111","promptId":"p1","message":{"role":"user","content":"push when the tests pass"}}""" + "\n");
        return path;
    }

    JsonObject Payload(string hookEvent, string command, string? callId = "toolu_1") {
        var node = new JsonObject {
            ["hook_event_name"] = hookEvent, ["session_id"] = Sid, ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = command }, ["cwd"] = Tmp.PathTo("repo"),
            ["transcript_path"] = Transcript(),
        };
        if (callId is not null) node["tool_use_id"] = callId;
        return node;
    }

    void Judge(string outcome, int status = 200, int delayMs = 0) {
        var response = Response.Create().WithStatusCode(status).WithBody($$"""
            {"outcome":"{{outcome}}","model_outcome":"{{outcome}}","rationale":"the user asked for it",
             "user_authorization":"high","clamped":null,"lineage":"established","cache":"miss","failure_class":null,
             "consultation_id":"c-1","artifact_id":"a-1",
             "classifier":{"base_prompt_version":"policy-judge-v1","context_builder_version":"ledger-v1"},"latency_ms":12}
            """);
        if (delayMs > 0) response = response.WithDelay(delayMs);
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(response);
    }

    List<JsonNode> JudgeRequests() => [.. _server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)];

    List<JsonNode> Decisions() => SpooledPolicyEvents.Decisions(Config.Root, Sid);

    [Test]
    public async Task An_unmatched_call_takes_the_judges_allow_and_records_its_consultation() {
        WriteUserPolicy(JudgeOn);
        Judge("allow");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, stdout, Ample);

        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(hso["permissionDecisionReason"]!.GetValue<string>()).Contains("the user asked for it");

        var evt = Decisions().Single();
        await Assert.That(evt["effective_outcome"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(evt["judge"]!["consultation_id"]!.GetValue<string>()).IsEqualTo("c-1");
        await Assert.That(evt["judge"]!["artifact_id"]!.GetValue<string>()).IsEqualTo("a-1");
        await Assert.That(evt["judge"]!["lineage"]!.GetValue<string>()).IsEqualTo("established");
    }

    [Test]
    public async Task The_request_declares_turns_refusals_and_the_undelivered_snapshot() {
        WriteUserPolicy(JudgeOn);
        Judge("ask");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, new StringWriter(), Ample);

        var request = JudgeRequests().Single();
        await Assert.That(request["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
        await Assert.That(request["agent_id"]).IsNull();
        await Assert.That(request["turns"]!["user_messages"]![0]!["id"]!.GetValue<string>())
            .IsEqualTo("11111111-1111-1111-1111-111111111111");
        await Assert.That(request["turns"]!["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_1");
        await Assert.That(request["refusals"]!["complete"]!.GetValue<bool>()).IsTrue();
        await Assert.That(request["refusals"]!["source"]!.GetValue<string>()).IsEqualTo("transcript");
        await Assert.That(request["snapshot"]!["snapshot_id"]!.GetValue<string>())
            .IsEqualTo(request["snapshot_id"]!.GetValue<string>());
        await Assert.That(request["budget_ms"]!.GetValue<int>()).IsGreaterThan(0);
    }

    /// <summary>Once the drain has delivered the snapshot the server resolves it itself, so the
    /// request stops carrying it.</summary>
    [Test]
    public async Task A_delivered_snapshot_is_not_sent_inline() {
        WriteUserPolicy(JudgeOn);
        Judge("ask");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, new StringWriter(), Ample);
        var upload = SpooledPolicyEvents.Snapshots(Config.Root, Sid).Single().ToJsonString();
        PolicyDecisionEmitter.MarkSnapshotDelivered(Config.Root, upload);

        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push", "toolu_2").ToJsonString(), Sid, false, new StringWriter(), Ample);

        var requests = JudgeRequests();
        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[1]["snapshot"]).IsNull();
    }

    [Test]
    public async Task Uncertain_passes_through_and_is_recorded_with_why() {
        WriteUserPolicy(JudgeOn);
        Judge("uncertain");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, stdout, Ample);

        await Assert.That(stdout.ToString()).IsEmpty();
        var evt = Decisions().Single();
        await Assert.That(evt["effective_outcome"]!.GetValue<string>()).IsEqualTo("pass_through");
        await Assert.That(evt["failure_class"]!.GetValue<string>()).IsEqualTo(PolicyJudgeResult.Uncertain);
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    /// <summary>An older or gated server answers 404 or 405 with no coded body.</summary>
    [Test]
    public async Task A_server_without_the_route_passes_through() {
        WriteUserPolicy(JudgeOn);
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, stdout, Ample);

        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions().Single()["failure_class"]!.GetValue<string>()).IsEqualTo(PolicyJudgeResult.HttpError);
    }

    [Test]
    public async Task A_judge_slower_than_the_budget_passes_through_inside_it() {
        WriteUserPolicy(JudgeOn);
        Judge("deny", delayMs: 5_000);
        var stdout = new StringWriter();
        var started = TimeProvider.System.GetTimestamp();
        await Seam.HandlePreToolUseAsync(
            Payload("PreToolUse", "git push").ToJsonString(), Sid, false, stdout, TimeSpan.FromMilliseconds(400));

        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(3));
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions().Single()["failure_class"]!.GetValue<string>()).IsEqualTo(PolicyJudgeResult.Timeout);
    }

    [Test]
    public async Task A_policy_that_does_not_enable_the_judge_never_consults_it() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        Judge("deny");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, new StringWriter(), Ample);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(1);
    }

    [Test]
    public async Task A_rule_decision_never_consults_the_judge() {
        WriteUserPolicy(JudgeOn + "rules:\n  - match: { kind: shell, command: \"git push*\" }\n    outcome: ask\n");
        Judge("allow");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, stdout, Ample);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["permissionDecision"]!
            .GetValue<string>()).IsEqualTo("ask");
    }

    /// <summary>A rendered session evaluates tighten-only locally; the judge never runs there.</summary>
    [Test]
    public async Task A_rendered_session_never_consults_the_judge() {
        WriteUserPolicy(JudgeOn);
        Judge("allow");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, renderedAgent: true, new StringWriter(), Ample);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_judge_ask_at_pre_tool_use_is_journaled_so_its_prompt_stands() {
        WriteUserPolicy(JudgeOn);
        Judge("ask");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, new StringWriter(), Ample);

        _server.Reset();
        Judge("allow");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(Payload("PermissionRequest", "git push", callId: null), Sid, stdout, Ample);

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        var evt = Decisions()[^1];
        await Assert.That(evt["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(evt["pending_ask_consumed"]!.GetValue<bool>()).IsTrue();
        await Assert.That(evt["fresh_outcome"]!.GetValue<string>()).IsEqualTo("allow");
    }

    /// <summary>Under the ask-only fallback a prompt may consult the judge again, and a deny there
    /// tightens a standing prompt the earlier ask forced.</summary>
    [Test]
    public async Task A_judge_deny_at_the_prompt_outranks_a_consumed_ask() {
        WriteUserPolicy(JudgeOn);
        Judge("ask");
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", "git push").ToJsonString(), Sid, false, new StringWriter(), Ample);

        _server.Reset();
        Judge("deny");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(Payload("PermissionRequest", "git push", callId: null), Sid, stdout, Ample);

        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["decision"]!["behavior"]!
            .GetValue<string>()).IsEqualTo("deny");
        var request = JudgeRequests().Single();
        await Assert.That(request["seam"]!.GetValue<string>()).IsEqualTo("claude_permission_request");
        await Assert.That(request["turns"]!["tool_use_id"]).IsNull();
    }

    [Test]
    public async Task A_judge_allow_answers_an_unforced_prompt() {
        WriteUserPolicy(JudgeOn);
        Judge("allow");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(Payload("PermissionRequest", "git push", callId: null), Sid, stdout, Ample);

        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["decision"]!["behavior"]!
            .GetValue<string>()).IsEqualTo("allow");
        await Assert.That(Decisions().Single()["judge"]!["consultation_id"]!.GetValue<string>()).IsEqualTo("c-1");
    }
}
