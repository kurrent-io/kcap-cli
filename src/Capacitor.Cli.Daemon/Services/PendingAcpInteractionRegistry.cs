using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Correlates an ACP interaction request with the server's later
/// <c>AcpInteractionResolved</c> push. Copy-shape of <see cref="PendingPermissionRegistry"/>
/// parameterized on <see cref="AcpInteractionDecision"/> instead of <see cref="PermissionDecision"/>
/// — kept as a separate small class (matching this codebase's existing style of small,
/// non-generic sibling classes rather than a shared generic base) rather than genericizing
/// <see cref="PendingPermissionRegistry"/>, so the two registries can evolve independently (e.g.
/// if ACP interactions later need multi-recipient fan-out that Claude Code permissions never
/// will) without either one's tests coupling to the other's generic parameter.
/// </summary>
internal sealed class PendingAcpInteractionRegistry {
    const int MaxBufferedDecisions = 1024;

    readonly Lock                                                              _gate       = new();
    readonly Dictionary<string, TaskCompletionSource<AcpInteractionDecision>>  _pending    = new();
    readonly Dictionary<string, AcpInteractionDecision>                        _early      = new();
    readonly Queue<string>                                                     _earlyOrder = new();
    // Request ids this side stopped waiting on. The server still answers them — at least with the
    // cancel this side asked for — and that answer must be dropped, not buffered: request ids are a
    // per-server sequence, so a buffered stale decision could be handed to a later request that reuses
    // the id after a server restart. A new wait on the id supersedes the mark.
    readonly HashSet<string>                                                   _abandoned      = new();
    readonly Queue<string>                                                     _abandonedOrder = new();

    public Task<AcpInteractionDecision> AwaitDecisionAsync(string requestId, CancellationToken ct) {
        TaskCompletionSource<AcpInteractionDecision> tcs;

        lock (_gate) {
            _abandoned.Remove(requestId);
            if (_early.Remove(requestId, out var early))
                return Task.FromResult(early);

            tcs = new TaskCompletionSource<AcpInteractionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
        }

        return AwaitWithCancellationAsync(requestId, tcs, ct);
    }

    async Task<AcpInteractionDecision> AwaitWithCancellationAsync(
            string                                        requestId,
            TaskCompletionSource<AcpInteractionDecision>   tcs,
            CancellationToken                              ct
        ) {
        await using var _ = ct.Register(() => {
            lock (_gate) {
                if (_pending.Remove(requestId)) MarkAbandoned(requestId);
            }

            tcs.TrySetCanceled(ct);
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    void MarkAbandoned(string requestId) {
        if (_abandoned.Add(requestId)) _abandonedOrder.Enqueue(requestId);
        while (_abandoned.Count > MaxBufferedDecisions && _abandonedOrder.Count > 0)
            _abandoned.Remove(_abandonedOrder.Dequeue());
    }

    public void Resolve(string requestId, AcpInteractionDecision decision) {
        TaskCompletionSource<AcpInteractionDecision>? tcs;

        lock (_gate) {
            if (_abandoned.Remove(requestId))
                return;

            if (!_pending.Remove(requestId, out tcs)) {
                if (_early.TryAdd(requestId, decision))
                    _earlyOrder.Enqueue(requestId);
                else
                    _early[requestId] = decision;

                while (_early.Count > MaxBufferedDecisions && _earlyOrder.Count > 0)
                    _early.Remove(_earlyOrder.Dequeue());

                return;
            }
        }

        tcs!.TrySetResult(decision);
    }
}
