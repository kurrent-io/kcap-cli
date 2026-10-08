using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// The judge at the ACP permission seam against a stub server: consulted for a call no rule decided,
/// its answer taking the place a rule's would, and every request windowless and declaring the
/// refusals this bridge itself relayed.
/// </summary>
public class AcpInteractionBridgeJudgeTests : IDisposable {
    const string AgentId      = "agent-1";
    const string AcpSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static PolicySnapshot Snapshot(string yaml) => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", yaml,
            PolicyDocumentBinder.Bind(yaml, PolicyScope.Repo))], false, []);

    static PolicySnapshot JudgeOn => Snapshot("""
        version: 1
        judge:
          mode: unmatched
        rules:
          - match: { kind: shell, command: "git status *" }
            outcome: allow
        """);

    static readonly (string Id, string OptKind)[] Standard =
        [("allow-once", "allow_once"), ("allow-always", "allow_always"), ("reject-once", "reject_once")];

    static JsonElement ShellFrame(string command, string? callId = "call-1") {
        var toolCall = new JsonObject { ["title"] = "Run", ["kind"] = "execute", ["rawInput"] = new JsonObject { ["command"] = command } };
        if (callId is not null) toolCall["toolCallId"] = callId;
        var options = new JsonArray([.. Standard.Select(o => (JsonNode)new JsonObject { ["optionId"] = o.Id, ["name"] = o.Id, ["kind"] = o.OptKind })]);
        return JsonDocument.Parse(new JsonObject {
            ["sessionId"] = AcpSessionId, ["toolCall"] = toolCall, ["options"] = options,
        }.ToJsonString()).RootElement.Clone();
    }

    sealed class Harness {
        public AcpInteractionBridge        Bridge    { get; init; } = null!;
        public List<PolicyDecisionEventV1> Decisions { get; } = [];
        public List<AcpAutoApprovalNotice> Notices   { get; } = [];
        public int                         Forwarded { get; set; }

        /// <summary>What the human answers each forwarded prompt with.</summary>
        public AcpInteractionDecision HumanAnswer { get; set; } = new("cancel", null, null, null, null, null);

        public async Task<JsonElement> HandleAsync(JsonElement paramsElement) {
            var result = await Bridge.HandleAsync(
                new AcpRequest(1, "session/request_permission", paramsElement), CancellationToken.None);

            return result!.Value.GetProperty("outcome");
        }
    }

    Harness Build(PolicySnapshot snapshot, string? presetToken = null, bool withJudge = true, TimeSpan? budget = null) {
        AcpLaunchPermissionPreset? preset = null;
        if (presetToken is not null) AcpPermissionPresets.TryResolve(presetToken, out preset);

        var gateway = new PolicyJudgeGateway(
            () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), _server.Url, TimeProvider.System);

        Harness harness = null!;
        harness = new Harness {
            Bridge = new AcpInteractionBridge(
                requestInteraction: (_, _) => {
                    harness.Forwarded++;
                    return Task.FromResult(harness.HumanAnswer);
                },
                agentId: AgentId,
                logger: NullLogger.Instance,
                time: TimeProvider.System,
                preset: preset,
                notifyAutoApproval: n => harness.Notices.Add(n),
                policySnapshot: snapshot,
                policyVendor: "cursor",
                notifyPolicyDecision: e => harness.Decisions.Add(e),
                policyCwd: "/wt",
                policyJudge: withJudge ? gateway : null,
                judgeBudget: budget ?? TimeSpan.FromSeconds(30)),
        };

        return harness;
    }

    void Judge(string outcome, int status = 200, int delayMs = 0) {
        var response = Response.Create().WithStatusCode(status).WithBody($$"""
            {"outcome":"{{outcome}}","model_outcome":"{{outcome}}","rationale":"r","user_authorization":"unknown",
             "clamped":null,"lineage":"none","cache":"miss","failure_class":null,"consultation_id":"c-1","artifact_id":"a-1"}
            """);
        if (delayMs > 0) response = response.WithDelay(delayMs);
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(response);
    }

    List<JsonNode> JudgeRequests() => [.. _server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)];

    [Test]
    public async Task A_judge_allow_selects_the_single_allow_once() {
        Judge("allow");
        var h = Build(JudgeOn);

        var outcome = await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-once");
        await Assert.That(h.Forwarded).IsEqualTo(0);
        var evt = h.Decisions.Single();
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("allow");
        await Assert.That(evt.MatchedRules).IsEmpty();
        await Assert.That(evt.Judge!.ConsultationId).IsEqualTo("c-1");
    }

    [Test]
    public async Task A_judge_deny_answers_with_the_agents_reject_option() {
        Judge("deny");
        var h = Build(JudgeOn);

        var outcome = await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("reject-once");
        await Assert.That(h.Forwarded).IsEqualTo(0);
        await Assert.That(h.Decisions.Single().EffectiveOutcome).IsEqualTo("deny");
    }

    [Test]
    public async Task A_judge_ask_parks_past_a_preset_that_would_approve_the_kind() {
        Judge("ask");
        var h = Build(JudgeOn, AcpPermissionPresets.Edit);

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(h.Forwarded).IsEqualTo(1);
        await Assert.That(h.Notices).IsEmpty();
        var evt = h.Decisions.Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("ask");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("parked");
    }

    [Test]
    public async Task Uncertain_passes_through_to_the_layers_below_and_is_recorded_with_why() {
        Judge("uncertain");
        var h = Build(JudgeOn);

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(h.Forwarded).IsEqualTo(1);
        var evt = h.Decisions.Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("pass_through");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("pass_through");
        await Assert.That(evt.FailureClass).IsEqualTo(PolicyJudgeResult.Uncertain);
    }

    [Test]
    public async Task A_server_without_the_route_passes_through() {
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));
        var h = Build(JudgeOn);

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(h.Forwarded).IsEqualTo(1);
        await Assert.That(h.Decisions.Single().FailureClass).IsEqualTo(PolicyJudgeResult.HttpError);
    }

    /// <summary>The timeout class is only ever the client's own deadline firing, so it proves the
    /// seam stopped waiting before the stub's answer arrived.</summary>
    [Test]
    public async Task A_judge_slower_than_the_budget_passes_through_inside_it() {
        Judge("deny", delayMs: 10_000);
        var h = Build(JudgeOn, budget: TimeSpan.FromMilliseconds(400));

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(h.Forwarded).IsEqualTo(1);
        await Assert.That(h.Decisions.Single().FailureClass).IsEqualTo(PolicyJudgeResult.Timeout);
    }

    /// <summary>An ACP agent has no transcript the server can verify a turn against, and the hosted
    /// run's snapshot is staged under its agent id.</summary>
    [Test]
    public async Task The_request_is_windowless_names_the_hosted_run_and_carries_no_snapshot() {
        Judge("ask");
        var h = Build(JudgeOn);

        await h.HandleAsync(ShellFrame("git push"));

        var request = JudgeRequests().Single();
        await Assert.That(request["session_id"]!.GetValue<string>()).IsEqualTo(AcpSessionId);
        await Assert.That(request["agent_id"]!.GetValue<string>()).IsEqualTo(AgentId);
        await Assert.That(request["vendor"]!.GetValue<string>()).IsEqualTo("cursor");
        await Assert.That(request["seam"]!.GetValue<string>()).IsEqualTo("acp_request_permission");
        await Assert.That(request["turns"]).IsNull();
        await Assert.That(request["snapshot"]).IsNull();
        await Assert.That(request["refusals"]!["source"]!.GetValue<string>()).IsEqualTo("bridge");
        await Assert.That(request["refusals"]!["complete"]!.GetValue<bool>()).IsTrue();
        await Assert.That(request["refusals"]!["entries"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_call_the_human_refused_through_the_bridge_is_declared_on_the_next_request() {
        Judge("uncertain");
        var h = Build(JudgeOn);
        h.HumanAnswer = new AcpInteractionDecision("deny", "reject-once", null, null, null, null);

        await h.HandleAsync(ShellFrame("rm -rf build", "call-1"));
        await h.HandleAsync(ShellFrame("git push", "call-2"));

        var refusals = JudgeRequests()[1]["refusals"]!;
        await Assert.That(refusals["complete"]!.GetValue<bool>()).IsTrue();
        var entry = refusals["entries"]!.AsArray().Single()!;
        await Assert.That(entry["tool_use_id"]!.GetValue<string>()).IsEqualTo("call-1");
        await Assert.That(entry["tool"]!.GetValue<string>()).IsEqualTo("execute");
        await Assert.That(entry["target"]!.GetValue<string>()).IsEqualTo("rm -rf build");
    }

    [Test]
    public async Task A_reject_option_picked_under_an_affirmative_outcome_is_a_refusal() {
        Judge("uncertain");
        var h = Build(JudgeOn);
        h.HumanAnswer = new AcpInteractionDecision("allow", "reject-once", null, null, null, null);

        await h.HandleAsync(ShellFrame("rm -rf build", "call-1"));
        await h.HandleAsync(ShellFrame("git push", "call-2"));

        await Assert.That(JudgeRequests()[1]["refusals"]!["entries"]!.AsArray().Count).IsEqualTo(1);
    }

    /// <summary>A cancel is the turn going away, and an allow is not a no.</summary>
    [Test]
    [Arguments("cancel", null)]
    [Arguments("allow_once", "allow-once")]
    public async Task An_answer_that_is_not_a_no_declares_nothing(string outcome, string? optionId) {
        Judge("uncertain");
        var h = Build(JudgeOn);
        h.HumanAnswer = new AcpInteractionDecision(outcome, optionId, null, null, null, null);

        await h.HandleAsync(ShellFrame("rm -rf build", "call-1"));
        await h.HandleAsync(ShellFrame("git push", "call-2"));

        var refusals = JudgeRequests()[1]["refusals"]!;
        await Assert.That(refusals["complete"]!.GetValue<bool>()).IsTrue();
        await Assert.That(refusals["entries"]!.AsArray().Count).IsEqualTo(0);
    }

    /// <summary>A refusal the bridge cannot name still happened, so the history is incomplete
    /// rather than shorter.</summary>
    [Test]
    public async Task A_refused_call_with_no_tool_call_id_makes_the_refusals_incomplete() {
        Judge("uncertain");
        var h = Build(JudgeOn);
        h.HumanAnswer = new AcpInteractionDecision("deny", "reject-once", null, null, null, null);

        await h.HandleAsync(ShellFrame("rm -rf build", callId: null));
        await h.HandleAsync(ShellFrame("git push", "call-2"));

        await Assert.That(JudgeRequests()[1]["refusals"]!["complete"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task A_rule_decision_never_consults_the_judge() {
        Judge("deny");
        var h = Build(JudgeOn);

        var outcome = await h.HandleAsync(ShellFrame("git status"));

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-once");
        await Assert.That(h.Decisions.Single().Judge).IsNull();
    }

    [Test]
    public async Task A_policy_that_does_not_enable_the_judge_never_consults_it() {
        Judge("deny");
        var h = Build(Snapshot("version: 1\nrules: []\n"));

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(h.Forwarded).IsEqualTo(1);
        await Assert.That(h.Decisions).IsEmpty();
    }

    [Test]
    public async Task No_gateway_leaves_an_undecided_call_exactly_as_it_was() {
        var h = Build(JudgeOn, AcpPermissionPresets.Explore, withJudge: false);

        await h.HandleAsync(ShellFrame("git push"));

        await Assert.That(h.Forwarded).IsEqualTo(1);
        await Assert.That(h.Decisions).IsEmpty();
    }
}
