using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>The eight non-Claude harnesses' SessionStart context provider: one fragment from the memory,
/// guidelines and flows lanes, the memory marker first. Scope is resolved once and the enabled lanes run in
/// parallel under one budget; a disabled lane contributes nothing and never blocks commit.</summary>
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

        using var expiry = new CancellationTokenSource(request.Budget, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, expiry.Token);

        var flowsTask = request.FlowsDisabled ? null : RunLaneAsync(() => flows.FetchAsync(request, cts.Token));

        // Flows ignore scope, and a slow scope resolution must not spend the budget they share.
        if (request.Disabled && request.GuidelinesDisabled)
            return Combine(null, null, flowsTask is null ? null : await flowsTask);

        SessionStartMemoryScope scope;
        try {
            scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart scope resolution skipped: {ex.Message}");
            if (flowsTask is not null) await flowsTask;

            return SessionStartMemoryContextResult.Retry;
        }

        // Started before any further await so the lanes share the budget in parallel.
        var memoryTask     = request.Disabled           ? null : RunLaneAsync(() => memory.FetchWithScopeAsync(scope, request, cts.Token));
        var guidelinesTask = request.GuidelinesDisabled ? null : RunLaneAsync(() => guidelines.FetchWithScopeAsync(scope, request, cts.Token));

        var memoryResult     = memoryTask     is null ? null : await memoryTask;
        var guidelinesResult = guidelinesTask is null ? null : await guidelinesTask;
        var flowsResult      = flowsTask      is null ? null : await flowsTask;

        return Combine(memoryResult, guidelinesResult, flowsResult);
    }

    async Task<SessionStartMemoryContextResult> RunLaneAsync(Func<Task<SessionStartMemoryContextResult>> lane) {
        try {
            return await lane();
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart context lane skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    // Flows content alone must not commit the lease while memory or guidelines want a retry.
    static SessionStartMemoryContextResult Combine(
            SessionStartMemoryContextResult? memoryResult, SessionStartMemoryContextResult? guidelinesResult, SessionStartMemoryContextResult? flowsResult) {
        var memoryFragment     = Fragment(memoryResult);
        var guidelinesFragment = Fragment(guidelinesResult);
        var flowsFragment      = Fragment(flowsResult);

        if (memoryFragment is not null || guidelinesFragment is not null)
            return Committed(Compose(memoryFragment, [guidelinesFragment, flowsFragment]));

        if (IsRetry(memoryResult) || IsRetry(guidelinesResult))
            return Retry([memoryResult, guidelinesResult, flowsResult]);

        if (flowsFragment is not null) return Committed(Compose(null, [flowsFragment]));
        if (IsRetry(flowsResult)) return Retry([flowsResult]);

        return SessionStartMemoryContextResult.Empty;

        static string? Fragment(SessionStartMemoryContextResult? r) =>
            r is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } f } ? f : null;

        static SessionStartMemoryContextResult Committed(string fragment) => new(SessionStartMemoryDisposition.Ready, fragment);

        static bool IsRetry(SessionStartMemoryContextResult? r) => r?.Disposition == SessionStartMemoryDisposition.RetryableFailure;

        static SessionStartMemoryContextResult Retry(SessionStartMemoryContextResult?[] all) =>
            new(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: all.Where(IsRetry).Max(r => r!.RetryAfter));
    }

    /// <summary>Marker first: Pi and OpenCode capture stdout only when it opens with it, and only the memory
    /// fragment carries it.</summary>
    static string Compose(string? memoryFragment, IReadOnlyList<string?> others) {
        var present = others.OfType<string>().ToList();
        var rest    = string.Join("\n\n", present);
        if (memoryFragment is null) return MemoryIndexEmitter.FragmentMarker + "\n" + rest;

        return present.Count == 0 ? memoryFragment : memoryFragment + "\n\n" + rest;
    }

    static bool IsFailOpen(Exception ex) =>
        ex is HttpRequestException or IOException or JsonException or
              OperationCanceledException or UnauthorizedAccessException or InvalidDataException;
}
