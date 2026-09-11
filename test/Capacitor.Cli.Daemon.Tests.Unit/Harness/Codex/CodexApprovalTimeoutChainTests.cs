using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>Composes the bridge with the REAL pieces its production delegate is made of — the connection
/// retry helper and the pending-interaction registry — instead of a fake that honours the token by
/// construction. A decision that never arrives must still become a decline at the bridge's timeout.</summary>
public class CodexApprovalTimeoutChainTests {
    static AcpRequest Approval() {
        using var doc = JsonDocument.Parse("""{"threadId":"t-1","itemId":"i-1","command":"pwd"}""");
        return new AcpRequest(0, "item/commandExecution/requestApproval", doc.RootElement.Clone());
    }

    [Test]
    public async Task An_unanswered_interaction_declines_at_the_timeout_through_the_real_registry_and_retry() {
        var registry = new PendingAcpInteractionRegistry();

        // Mirrors ServerConnection.RequestAcpInteractionAsync: invoke (returns the request id) then await the decision.
        async Task<AcpInteractionDecision> RequestInteraction(AcpInteractionRequest request, CancellationToken ct) {
            var requestId = await ConnectionRetry.InvokeWithConnectionRetryAsync(
                () => Task.FromResult("req-1"), () => true, TimeSpan.FromMilliseconds(50), _ => { }, ct);
            return await registry.AwaitDecisionAsync(requestId, ct);
        }

        var bridge = new CodexApprovalBridge(RequestInteraction, "agent-1", NullLogger.Instance, TimeSpan.FromMilliseconds(300));

        var result = await bridge.HandleAsync(Approval(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result?.GetProperty("decision").GetString()).IsEqualTo("decline");
    }
}
