using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The server keeps an ACP interaction open until something answers it. When this side stops
/// waiting, the entry has to be resolved from here or the pending card outlives the request.</summary>
public class ServerConnectionAbandonedInteractionTests {
    sealed class CapturingServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance) {
        public readonly List<(string SessionId, string RequestId, string Behavior)> Responded = [];
        public readonly TaskCompletionSource RespondedOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) {
            Responded.Add((sessionId, serverRequestId, decision.Behavior));
            RespondedOnce.TrySetResult();
            return Task.FromResult(new RespondOutcome(RespondOutcomeKind.Applied, null));
        }
    }

    static AcpInteractionRequest Interaction(string threadId) =>
        new(AgentId: "agent-1", AcpSessionId: threadId, Kind: "permission", ToolName: "commandExecution",
            ToolInput: null, ToolCallId: "item-1", Prompt: null, Options: [], IsMultiSelect: false);

    [Test]
    public async Task A_wait_that_is_cancelled_resolves_the_server_side_interaction_as_cancelled() {
        await using var connection = new CapturingServerConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await connection.AwaitInteractionDecisionAsync(
                Interaction("01a08e07-957d-7b13-a935-093cb48cb814"), "sid:4", cts.Token))
            .Throws<OperationCanceledException>();

        await connection.RespondedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.Responded).HasSingleItem();
        var (sessionId, requestId, behavior) = connection.Responded[0];
        // The thread id travels in UUID form; the server keys interactions by the canonical 32-hex id.
        await Assert.That(sessionId).IsEqualTo("01a08e07957d7b13a935093cb48cb814");
        await Assert.That(requestId).IsEqualTo("sid:4");
        await Assert.That(behavior).IsEqualTo(ServerConnection.AbandonedInteractionOutcome);
    }
}
