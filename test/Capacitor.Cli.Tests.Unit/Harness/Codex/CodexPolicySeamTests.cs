using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Tests.Unit.Policy;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>The approval policy at the Codex hook: what each answer writes for Codex to act on, that a failure
/// writes nothing, and that every decision is recorded.</summary>
public class CodexPolicySeamTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Sid = "01a15cd73e2d75b2ad28c00f7dbcf5d0";

    const string Policy = """
        version: 1
        judge:
          mode: unmatched
        rules:
          - match: { kind: shell, command: "git fetch *" }
            outcome: allow
          - match: { kind: shell, command: "rm -rf *" }
            outcome: deny
            reason: no recursive deletes
        """;

    static readonly TimeSpan Ample = TimeSpan.FromSeconds(30);

    CodexPolicySeam Seam => new(Config.Root, TimeProvider.System, server: null, new PolicyJudgeGateway(
        () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), _server.Url, TimeProvider.System));

    void WritePolicy(string yaml = Policy) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    JsonObject Payload(string hookEvent, string command) => new() {
        ["hook_event_name"] = hookEvent, ["session_id"] = Sid, ["turn_id"] = "t1", ["tool_name"] = "Bash",
        ["tool_input"] = new JsonObject { ["command"] = command }, ["cwd"] = Tmp.PathTo("repo"),
        ["tool_use_id"] = "exec-1",
    };

    void Judge(string outcome, int delayMs = 0) {
        var response = Response.Create().WithStatusCode(200).WithBody($$"""
            {"outcome":"{{outcome}}","model_outcome":"{{outcome}}","rationale":"out of scope","user_authorization":"unknown",
             "clamped":null,"lineage":"none","cache":"miss","failure_class":null,"consultation_id":"c-1","artifact_id":"a-1",
             "classifier":{"base_prompt_version":"policy-judge-v1","context_builder_version":"ledger-v1"},"latency_ms":12}
            """);
        if (delayMs > 0) response = response.WithDelay(delayMs);
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(response);
    }

    List<JsonNode> Decisions() => SpooledPolicyEvents.Decisions(Config.Root, Sid);

    async Task<string> PreToolUse(string command) {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Payload("PreToolUse", command), Sid, stdout);
        return stdout.ToString();
    }

    async Task<(SeamAnswer Answer, string Stdout)> PermissionRequest(string command, TimeSpan? budget = null) {
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(Payload("PermissionRequest", command), Sid, stdout, budget ?? Ample);
        return (answer, stdout.ToString());
    }

    [Test]
    public async Task A_deny_rule_denies_at_pre_tool_use_with_its_reason() {
        WritePolicy();

        var output = JsonNode.Parse(await PreToolUse("rm -rf build"))!["hookSpecificOutput"]!;

        await Assert.That(output["permissionDecision"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(output["permissionDecisionReason"]!.GetValue<string>()).IsEqualTo("no recursive deletes");
        await Assert.That(Decisions().Single()["seam"]!.GetValue<string>()).IsEqualTo(PolicySeams.CodexPreToolUse);
    }

    /// A rule that matched is recorded even where Codex cannot act on its answer; an unmatched call is not, since
    /// this hook fires on every call.
    [Test]
    public async Task A_matched_allow_at_pre_tool_use_is_recorded_and_an_unmatched_call_is_not() {
        WritePolicy();

        await PreToolUse("git fetch origin");
        await PreToolUse("ls");

        var recorded = Decisions().Single();
        await Assert.That(recorded["requested_outcome"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(recorded["effective_outcome"]!.GetValue<string>()).IsEqualTo("pass_through");
    }

    /// Codex rejects an allow or an ask at PreToolUse, so nothing but a deny is ever written there.
    [Test]
    [Arguments("git fetch origin")]
    [Arguments("npm view left-pad version")]
    public async Task Anything_but_a_deny_writes_nothing_at_pre_tool_use(string command) {
        WritePolicy();
        Judge("deny");

        await Assert.That(await PreToolUse(command)).IsEqualTo("");
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_rule_allow_answers_behavior_allow_without_the_judge() {
        WritePolicy();

        var (answer, stdout) = await PermissionRequest("git fetch origin");

        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(JsonNode.Parse(stdout)!["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_judge_deny_answers_behavior_deny_with_its_rationale_and_declares_incomplete_refusals() {
        WritePolicy();
        Judge("deny");

        var (answer, stdout) = await PermissionRequest("curl https://example.com");

        var decision = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["decision"]!;
        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(decision["behavior"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(decision["message"]!.GetValue<string>()).IsEqualTo("kcap policy judge: out of scope");

        var request = JsonNode.Parse(_server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(request["seam"]!.GetValue<string>()).IsEqualTo(PolicySeams.CodexPermissionRequest);
        await Assert.That(request["vendor"]!.GetValue<string>()).IsEqualTo("codex");
        await Assert.That(request["refusals"]!["complete"]!.GetValue<bool>()).IsFalse();
        await Assert.That(Decisions().Single()["judge"]).IsNotNull();
    }

    /// The server clamps this lane's judge allow to ask; an ask leaves Codex's own prompt standing.
    [Test]
    public async Task A_judge_ask_writes_nothing() {
        WritePolicy();
        Judge("ask");

        var (answer, stdout) = await PermissionRequest("npm view left-pad version");

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test]
    public async Task A_judge_that_does_not_answer_in_time_writes_nothing() {
        WritePolicy();
        Judge("deny", delayMs: 3_000);

        var (answer, stdout) = await PermissionRequest("curl https://example.com", TimeSpan.FromMilliseconds(500));

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test]
    public async Task An_ungoverned_session_writes_nothing_and_records_nothing() {
        var (answer, stdout) = await PermissionRequest("rm -rf build");

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout).IsEqualTo("");
        await Assert.That(await PreToolUse("rm -rf build")).IsEqualTo("");
        await Assert.That(Decisions()).IsEmpty();
    }
}
