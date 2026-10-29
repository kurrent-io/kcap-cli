using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>The bridge with the launch's policy ahead of the card: a decided approval never reaches a card, a
/// user's decline at the card is declared to the judge, and a permissions grant stays the human's alone.</summary>
public class CodexApprovalBridgePolicyTests {
    const string ThreadId = "01a15cd7-3e2d-75b2-ad28-c00f7dbcf5d0";
    static readonly string Session = SessionIds.Canonical(ThreadId)!;

    const string Policy = """
        version: 1
        rules:
          - match: { kind: shell, command: "git fetch *" }
            outcome: allow
          - match: { kind: shell, command: "rm -rf *" }
            outcome: deny
        """;

    readonly PermissionRefusalLedger _refusals = new();
    int _cards;

    CodexApprovalBridge Bridge(AcpInteractionDecision card) {
        var snapshot = new PolicySnapshot("snap-1", [
            new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", Policy, PolicyDocumentBinder.Bind(Policy, PolicyScope.Repo))], false, []);
        var seam = new CodexHostedPolicySeam(snapshot, null, _ => { }, _refusals, () => null, "agent-1", TimeProvider.System, NullLogger.Instance);
        seam.MarkBridgedSinceStart(ThreadId);
        return new((_, _) => { _cards++; return Task.FromResult(card); }, "agent-1", NullLogger.Instance, TimeSpan.FromSeconds(30),
            TimeProvider.System, seam);
    }

    static AcpRequest Command(string command) => new(1, "item/commandExecution/requestApproval", JsonDocument.Parse(
        $$"""{"threadId":"{{ThreadId}}","turnId":"turn-1","itemId":"exec-1","startedAtMs":1,"command":"{{command}}","cwd":"/wt"}""").RootElement.Clone());

    static readonly AcpInteractionDecision Allow   = new("allow", "accept", "Allow", null, null, null);
    static readonly AcpInteractionDecision Decline = new("reject_once", "decline", "Deny", null, null, null);

    static string? DecisionOf(JsonElement? result) => result!.Value.GetProperty("decision").GetString();

    [Test]
    [Arguments("git fetch origin", "accept")]
    [Arguments("rm -rf build", "decline")]
    public async Task A_policy_decision_never_reaches_a_card(string command, string expected) {
        var result = await Bridge(Allow).HandleAsync(Command(command), CancellationToken.None);

        await Assert.That(DecisionOf(result)).IsEqualTo(expected);
        await Assert.That(_cards).IsEqualTo(0);
    }

    [Test]
    public async Task An_undecided_approval_reaches_the_card_and_a_users_decline_is_declared() {
        var result = await Bridge(Decline).HandleAsync(Command("curl https://example.com"), CancellationToken.None);

        await Assert.That(DecisionOf(result)).IsEqualTo("decline");
        await Assert.That(_cards).IsEqualTo(1);
        await Assert.That(_refusals.Declare(Session).Entries.Single().PromptId).IsEqualTo("turn-1");
    }

    /// Codex sees a decline when the card times out or fails, so the judge is told of it too.
    [Test]
    public async Task A_card_that_fails_declines_and_is_declared() {
        var snapshot = new PolicySnapshot("snap-1", [
            new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", Policy, PolicyDocumentBinder.Bind(Policy, PolicyScope.Repo))], false, []);
        var seam = new CodexHostedPolicySeam(snapshot, null, _ => { }, _refusals, () => null, "agent-1", TimeProvider.System, NullLogger.Instance);
        seam.MarkBridgedSinceStart(ThreadId);
        var bridge = new CodexApprovalBridge((_, _) => throw new InvalidOperationException("card lost"), "agent-1", NullLogger.Instance,
            TimeSpan.FromSeconds(30), TimeProvider.System, seam);

        var result = await bridge.HandleAsync(Command("curl https://example.com"), CancellationToken.None);

        await Assert.That(DecisionOf(result)).IsEqualTo("decline");
        await Assert.That(_refusals.Declare(Session).Entries.Single().ToolUseId).IsEqualTo("exec-1");
    }

    [Test]
    public async Task A_permissions_grant_decline_is_not_a_refusal() {
        var grant = new AcpRequest(1, "item/permissions/requestApproval", JsonDocument.Parse(
            $$$"""{"threadId":"{{{ThreadId}}}","turnId":"turn-1","itemId":"perm-1","permissions":{"network":true}}""").RootElement.Clone());

        await Bridge(Decline).HandleAsync(grant, CancellationToken.None);

        await Assert.That(_cards).IsEqualTo(1);
        await Assert.That(_refusals.Declare(Session).Entries).IsEmpty();
    }
}
