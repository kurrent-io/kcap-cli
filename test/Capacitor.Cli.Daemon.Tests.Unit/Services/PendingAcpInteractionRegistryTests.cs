using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="PendingAcpInteractionRegistry"/> is a straight copy-shape of the already-
/// tested <see cref="PendingPermissionRegistry"/> parameterized on <see cref="AcpInteractionDecision"/>
/// — these tests mirror that class's own test coverage for the early-arrival race and cancellation.
/// </summary>
public class PendingAcpInteractionRegistryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Resolve_BeforeAwait_IsBufferedAndReturnedImmediately() {
        var registry = new PendingAcpInteractionRegistry();
        var decision = new AcpInteractionDecision("allow", null, null, null, null, null);

        registry.Resolve("req-1", decision);

        var result = await registry.AwaitDecisionAsync("req-1", CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(result.Outcome).IsEqualTo("allow");
    }

    [Test]
    public async Task AwaitDecisionAsync_CompletesWhenResolveCalledAfter() {
        var registry = new PendingAcpInteractionRegistry();
        var decision = new AcpInteractionDecision("deny", null, null, null, null, null);

        var awaitTask = registry.AwaitDecisionAsync("req-2", CancellationToken.None);
        registry.Resolve("req-2", decision);

        var result = await awaitTask.WaitAsync(HangGuard);

        await Assert.That(result.Outcome).IsEqualTo("deny");
    }

    [Test]
    public async Task AwaitDecisionAsync_CancellationTokenFires_ThrowsOperationCanceled() {
        var registry = new PendingAcpInteractionRegistry();
        using var cts = new CancellationTokenSource();

        var awaitTask = registry.AwaitDecisionAsync("req-3", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await awaitTask.WaitAsync(HangGuard));
    }

    /// <summary>A decision arriving for a request this side stopped waiting on is dropped, not
    /// buffered: the server echoes the cancel this side asked for, and request ids are a per-server
    /// sequence, so a buffered stale decision would be handed to whichever later request reuses the id.
    /// The reused id must still receive its own decision.</summary>
    [Test]
    public async Task A_resolution_for_an_abandoned_request_is_discarded_and_a_reused_id_still_pends() {
        var registry = new PendingAcpInteractionRegistry();
        using var cts = new CancellationTokenSource();
        var abandoned = registry.AwaitDecisionAsync("req-4", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await abandoned.WaitAsync(HangGuard));

        registry.Resolve("req-4", new AcpInteractionDecision("cancel", null, null, null, null, null)); // the server's echo

        var reused = registry.AwaitDecisionAsync("req-4", CancellationToken.None);
        await Assert.That(await Task.WhenAny(reused, Task.Delay(200)) == reused).IsFalse()
            .Because("the stale cancel must not resolve a later request that reuses the id");

        registry.Resolve("req-4", new AcpInteractionDecision("allow", null, null, null, null, null));
        await Assert.That((await reused.WaitAsync(HangGuard)).Outcome).IsEqualTo("allow");
    }
}
