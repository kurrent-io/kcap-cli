using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Correlates a hosted-agent permission request with the server's later decision push.
///
/// Background (AI-864): the daemon used to obtain a permission decision as the return value of
/// a <c>RequestPermission</c> hub invocation that stayed pending for the entire elicitation
/// wait. Under SignalR's default <c>MaximumParallelInvocationsPerClient = 1</c> that single
/// in-flight invocation starved the daemon's <c>DaemonPing</c>, tripped the 5s ping deadline,
/// and sent the daemon into a reconnect storm (garbled terminal + spurious deny). The new model
/// has the daemon call <c>RequestPermission2</c> — a short invocation returning a
/// <c>requestId</c> — and the server pushes the decision via a <c>PermissionResolved</c>
/// server→client message when the user responds. This registry holds the awaiting
/// <see cref="TaskCompletionSource{T}"/> keyed by requestId and bridges the push back to it.
///
/// The decision push can in principle arrive before the caller has registered its await (the
/// user answers in the gap between <c>RequestPermission2</c> returning and
/// <see cref="AwaitDecisionAsync"/> running). Such early arrivals are buffered and picked up at
/// registration, so no decision is lost. The buffer is bounded (<see cref="MaxBufferedDecisions"/>,
/// FIFO eviction) so duplicate/late/unknown pushes that are never awaited can't grow without
/// bound over the daemon's lifetime.
/// </summary>
internal sealed class PendingPermissionRegistry {
    /// <summary>
    /// Cap on buffered early/orphan decisions. The buffer normally holds a single entry for a
    /// sub-millisecond race; this cap only matters for pathological pushes (duplicates, late
    /// pushes after a cancelled await, unknown requestIds) that no one will ever await. Past the
    /// cap, the oldest buffered decision is evicted — it was never going to be claimed anyway.
    /// </summary>
    const int MaxBufferedDecisions = 1024;

    readonly Lock                                                         _gate       = new();
    readonly Dictionary<string, TaskCompletionSource<PermissionDecision>> _pending    = new();
    readonly Dictionary<string, PermissionDecision>                       _early      = new();
    readonly Queue<string>                                                _earlyOrder = new();

    /// <summary>
    /// Awaits the decision for <paramref name="requestId"/>. Returns immediately if the push
    /// already arrived (buffered); otherwise completes when <see cref="Resolve"/> is called for
    /// this id, or throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires
    /// first (daemon shutdown). The pending entry is removed on completion or cancellation.
    /// </summary>
    public Task<PermissionDecision> AwaitDecisionAsync(string requestId, CancellationToken ct) {
        TaskCompletionSource<PermissionDecision> tcs;

        lock (_gate) {
            // Fast path: the decision raced ahead of this registration.
            if (_early.Remove(requestId, out var early))
                return Task.FromResult(early);

            // Registration and Resolve are serialized by _gate, so once we add the pending entry a
            // concurrent Resolve will see it (no early/pending double-check needed).
            tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
        }

        return AwaitWithCancellationAsync(requestId, tcs, ct);
    }

    async Task<PermissionDecision> AwaitWithCancellationAsync(
            string                                   requestId,
            TaskCompletionSource<PermissionDecision> tcs,
            CancellationToken                        ct
        ) {
        await using var _ = ct.Register(() => {
            // Drop the entry so a later Resolve() buffers (and is bounded) rather than completing a
            // dead waiter, and unblock the caller.
            lock (_gate) _pending.Remove(requestId);

            tcs.TrySetCanceled(ct);
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers the user's decision for <paramref name="requestId"/>. Completes the awaiting call
    /// if one is registered; otherwise buffers the decision for an await that hasn't started yet.
    /// A decision with no current and no future waiter (duplicate push, or a request whose caller
    /// was cancelled) stays buffered only until evicted by the FIFO cap.
    /// </summary>
    public void Resolve(string requestId, PermissionDecision decision) {
        TaskCompletionSource<PermissionDecision>? tcs;

        lock (_gate) {
            if (!_pending.Remove(requestId, out tcs)) {
                if (_early.TryAdd(requestId, decision))
                    _earlyOrder.Enqueue(requestId);
                else
                    _early[requestId] = decision; // duplicate push: refresh value, keep queue slot

                // Evict oldest until within the cap. A dequeued id already consumed by an await is
                // simply absent from _early (Remove is a no-op), so the loop keeps going until it
                // has dropped enough live entries.
                while (_early.Count > MaxBufferedDecisions && _earlyOrder.Count > 0)
                    _early.Remove(_earlyOrder.Dequeue());

                return;
            }
        }

        tcs!.TrySetResult(decision);
    }
}
