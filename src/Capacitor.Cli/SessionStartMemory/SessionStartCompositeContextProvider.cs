using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>Every harness's SessionStart context provider: one fragment from the memory, guidelines and flows
/// lanes, the memory marker first; Claude's hook response carries its guidelines, so it runs without that
/// lane. Scope is resolved once and the enabled lanes run in parallel under one budget; a disabled lane
/// contributes nothing and never blocks commit.</summary>
internal sealed class SessionStartCompositeContextProvider(
    ISessionStartMemoryScopeResolver scopeResolver,
    SessionStartMemoryContextProvider memory,
    SessionStartGuidelinesLane guidelines,
    SessionStartFlowsLane flows,
    TimeProvider time,
    Action<string>? diagnostic = null) : ISessionStartContextProvider {

    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        if (request.AllLanesDisabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;

        // The lanes stop short of the budget, so what they return still reaches a caller waiting exactly that long.
        var cutoff = request.Budget > LaneReserve * 2 ? request.Budget - LaneReserve : request.Budget / 2;
        using var expiry = new CancellationTokenSource(cutoff, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, expiry.Token);

        var flowsTask = request.FlowsDisabled ? null : RunLaneAsync(() => flows.FetchAsync(request, cts.Token));

        // Flows ignore scope, and a slow scope resolution must not spend the budget they share.
        if (request.Disabled && request.GuidelinesDisabled)
            return Combine(null, null, ReportFlows(flowsTask is null ? null : await flowsTask));

        SessionStartMemoryScope scope;
        try {
            scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart scope resolution skipped: {ex.Message}");

            return Combine(SessionStartMemoryContextResult.Retry, null, ReportFlows(flowsTask is null ? null : await flowsTask));
        }

        // Started before any further await so the lanes share the budget in parallel.
        var memoryTask     = request.Disabled           ? null : RunLaneAsync(() => memory.FetchWithScopeAsync(scope, request, cts.Token));
        var guidelinesTask = request.GuidelinesDisabled ? null : RunLaneAsync(() => guidelines.FetchWithScopeAsync(scope, request, cts.Token));

        var memoryResult     = memoryTask     is null ? null : await memoryTask;
        var guidelinesResult = guidelinesTask is null ? null : await guidelinesTask;
        var flowsResult      = flowsTask      is null ? null : await flowsTask;

        return Combine(memoryResult, guidelinesResult, ReportFlows(flowsResult));
    }

    SessionStartMemoryContextResult? ReportFlows(SessionStartMemoryContextResult? flows) {
        if (flows?.Disposition == SessionStartMemoryDisposition.RetryableFailure)
            diagnostic?.Invoke("SessionStart flows lane skipped: no flow catalogue could be fetched or used from the cache");

        return flows;
    }

    async Task<SessionStartMemoryContextResult> RunLaneAsync(Func<Task<SessionStartMemoryContextResult>> lane) {
        try {
            return await lane();
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart context lane skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    // Flows content alone must not commit the lease while memory or guidelines want a retry, but it is
    // marker-less and safe to deliver again, so it rides along with the retry.
    static SessionStartMemoryContextResult Combine(
            SessionStartMemoryContextResult? memoryResult, SessionStartMemoryContextResult? guidelinesResult, SessionStartMemoryContextResult? flowsResult) {
        var memoryFragment     = Fragment(memoryResult);
        var guidelinesFragment = Fragment(guidelinesResult);
        // A flows retry can still carry a fragment of its own: the wake-up nudge, confirmed while the catalogue was not.
        var flowsFragment      = Fragment(flowsResult) ?? (IsRetry(flowsResult) ? flowsResult!.Fragment : null);

        if (memoryFragment is not null || guidelinesFragment is not null)
            return Committed(Compose(memoryFragment, guidelinesFragment, flowsFragment));

        if (IsRetry(memoryResult) || IsRetry(guidelinesResult) || IsRetry(flowsResult))
            return Retry([memoryResult, guidelinesResult, flowsResult]) with { Fragment = flowsFragment is null ? null : Compose(null, null, flowsFragment) };

        if (flowsFragment is not null) return Committed(Compose(null, null, flowsFragment));

        return SessionStartMemoryContextResult.Empty;

        static string? Fragment(SessionStartMemoryContextResult? r) =>
            r is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } f } ? f : null;

        static SessionStartMemoryContextResult Committed(string fragment) => new(SessionStartMemoryDisposition.Ready, fragment);

        static bool IsRetry(SessionStartMemoryContextResult? r) => r?.Disposition == SessionStartMemoryDisposition.RetryableFailure;

        static SessionStartMemoryContextResult Retry(SessionStartMemoryContextResult?[] all) =>
            new(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: all.Where(IsRetry).Max(r => r!.RetryAfter));
    }

    /// <summary>Marker first: Pi and OpenCode capture stdout only when it opens with it, and only the memory
    /// fragment carries it. Flows come straight after it, because a harness that caps its context cuts from
    /// the tail, and a long memory index must be what is cut rather than the offer.</summary>
    static string Compose(string? memoryFragment, string? guidelinesFragment, string? flowsFragment) {
        var memoryBody = memoryFragment is null ? null
            : memoryFragment.StartsWith(MemoryIndexEmitter.FragmentMarker, StringComparison.Ordinal)
                ? memoryFragment[MemoryIndexEmitter.FragmentMarker.Length..].TrimStart('\n')
                : memoryFragment;

        return MemoryIndexEmitter.FragmentMarker + "\n" + string.Join("\n\n", new[] { flowsFragment, memoryBody, guidelinesFragment }.Where(p => !string.IsNullOrEmpty(p)));
    }

    static readonly TimeSpan LaneReserve = TimeSpan.FromMilliseconds(250);

    static bool IsFailOpen(Exception ex) =>
        ex is HttpRequestException or IOException or JsonException or
              OperationCanceledException or UnauthorizedAccessException or InvalidDataException;
}
