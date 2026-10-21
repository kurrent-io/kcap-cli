using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The hosted seam's verdict on each approval, what it declares to the judge, and that anything it cannot
/// decide reaches the card unchanged.</summary>
public class CodexHostedPolicySeamTests : IDisposable {
    const string ThreadId = "01a15cd7-3e2d-75b2-ad28-c00f7dbcf5d0";
    static readonly string Session = SessionIds.Canonical(ThreadId)!;

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Policy = """
        version: 1
        judge:
          mode: unmatched
        rules:
          - match: { kind: shell, command: "git fetch *" }
            outcome: allow
          - match: { kind: shell, command: "rm -rf *" }
            outcome: deny
          - match: { kind: file_edit, path: "**/secrets/**" }
            outcome: deny
        """;

    static PolicySnapshot Snapshot(string yaml) => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", yaml, PolicyDocumentBinder.Bind(yaml, PolicyScope.Repo))], false, []);

    sealed class FakeProbe(params string[] stored) : IAcpPersistenceProbe {
        public Task<IReadOnlyDictionary<string, long>> AwaitPersistedAsync(IReadOnlyCollection<string> itemIds, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(itemIds.Where(stored.Contains).ToDictionary(i => i, _ => 1L));
    }

    readonly List<PolicyDecisionEventV1> _decisions = [];
    readonly PermissionRefusalLedger     _refusals  = new();

    CodexHostedPolicySeam Seam(IAcpPersistenceProbe? probe = null, bool withJudge = true) {
        var gateway = new PolicyJudgeGateway(
            () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), _server.Url, TimeProvider.System);
        _refusals.MarkBridgedSinceStart(Session);
        return new(Snapshot(Policy), withJudge ? gateway : null, _decisions.Add, _refusals, () => probe, "agent-1", TimeProvider.System,
            NullLogger.Instance);
    }

    void Judge(string outcome) =>
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""
            {"outcome":"{{outcome}}","model_outcome":"{{outcome}}","rationale":"r","user_authorization":"high","clamped":null,
             "lineage":"established","cache":"miss","failure_class":null,"consultation_id":"c-1","artifact_id":"a-1",
             "classifier":{"base_prompt_version":"policy-judge-v1","context_builder_version":"ledger-v1"},"latency_ms":12}
            """));

    static JsonElement Command(string command, string item = "exec-1", string? approval = null) => Element(new JsonObject {
        ["threadId"] = ThreadId, ["turnId"] = "turn-1", ["itemId"] = item, ["startedAtMs"] = 1, ["command"] = command, ["cwd"] = "/wt",
        ["approvalId"] = approval,
    });

    static JsonElement FileChange(string item) => Element(new JsonObject {
        ["threadId"] = ThreadId, ["turnId"] = "turn-1", ["itemId"] = item, ["startedAtMs"] = 1,
    });

    static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    const string CommandApproval = "item/commandExecution/requestApproval";
    const string FileApproval    = "item/fileChange/requestApproval";

    [Test]
    public async Task A_rule_allow_accepts_without_the_judge() {
        Judge("deny");

        await Assert.That(await Seam().EvaluateAsync(CommandApproval, Command("git fetch origin"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Accept);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(_decisions.Single().Seam).IsEqualTo(PolicySeams.HostedCodexApproval);
    }

    [Test]
    public async Task A_rule_deny_declines_and_is_declared_with_its_turn_and_approval() {
        await Assert.That(await Seam().EvaluateAsync(CommandApproval, Command("rm -rf build", approval: "a-1"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Decline);

        var refused = _refusals.Declare(Session).Entries.Single();
        await Assert.That(refused.ToolUseId).IsEqualTo("exec-1");
        await Assert.That(refused.PromptId).IsEqualTo("turn-1");
    }

    /// Without its paths a file change cannot be checked against a path rule, so the judge must not see it either.
    [Test]
    public async Task A_file_change_without_known_paths_reaches_the_card_unevaluated() {
        Judge("allow");

        await Assert.That(await Seam().EvaluateAsync(FileApproval, FileChange("patch-1"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Forward);
        await Assert.That(_server.LogEntries).IsEmpty();
        await Assert.That(_decisions).IsEmpty();
    }

    [Test]
    public async Task A_path_rule_applies_once_the_file_changes_paths_are_known() {
        var seam = Seam();
        seam.Paths.Observe("patch-1", Element(new JsonArray(new JsonObject { ["path"] = "/wt/config/secrets/key.txt", ["kind"] = "update" })));

        await Assert.That(await seam.EvaluateAsync(FileApproval, FileChange("patch-1"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Decline);
    }

    [Test]
    public async Task Only_stored_turns_are_declared_by_their_item_ids() {
        Judge("allow");
        var seam = Seam(new FakeProbe("m1"));
        seam.Turns.Observe(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "fix the build", ItemId: "m1", TurnId: "turn-1"));
        seam.Turns.Observe(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "now ship it", ItemId: "m2", TurnId: "turn-2"));

        await Assert.That(await seam.EvaluateAsync(CommandApproval, Command("npm view left-pad version"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Accept);

        var turns = JsonNode.Parse(_server.LogEntries.Single().RequestMessage.Body!)!["turns"]!["user_messages"]!.AsArray();
        await Assert.That(turns.Select(t => t!["id"]!.GetValue<string>())).IsEquivalentTo(new[] { AcpItemTurnId.For("m1").ToString() });
    }

    /// With no stored turn the consultation is windowless rather than declaring one the server cannot find.
    [Test]
    public async Task No_stored_turn_declares_no_turn_set() {
        Judge("ask");
        var seam = Seam(new FakeProbe());
        seam.Turns.Observe(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "fix the build", ItemId: "m1", TurnId: "turn-1"));

        await Assert.That(await seam.EvaluateAsync(CommandApproval, Command("npm view left-pad version"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Forward);

        await Assert.That(JsonNode.Parse(_server.LogEntries.Single().RequestMessage.Body!)!["turns"]).IsNull();
    }

    [Test]
    public async Task A_judge_failure_reaches_the_card() {
        _server.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost()).RespondWith(Response.Create().WithStatusCode(500));

        await Assert.That(await Seam().EvaluateAsync(CommandApproval, Command("npm view left-pad version"), CancellationToken.None))
            .IsEqualTo(CodexHostedVerdict.Forward);
    }

    [Test]
    public async Task A_users_decline_at_the_card_is_declared() {
        var seam = Seam();

        seam.RecordUserDecline(CommandApproval, Command("curl https://example.com", approval: "a-9"));

        await Assert.That(_refusals.Declare(Session).Entries.Single().ToolUseId).IsEqualTo("exec-1");
    }
}
