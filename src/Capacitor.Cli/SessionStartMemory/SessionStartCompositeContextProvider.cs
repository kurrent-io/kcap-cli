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

        SessionStartMemoryScope scope;
        try {
            scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart scope resolution skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }

        // Started before any await so the lanes share the budget in parallel.
        var memoryTask     = request.Disabled           ? null : RunLaneAsync(() => memory.FetchWithScopeAsync(scope, request, cts.Token));
        var guidelinesTask = request.GuidelinesDisabled ? null : RunLaneAsync(() => guidelines.FetchWithScopeAsync(scope, request, cts.Token));
        var flowsTask      = request.FlowsDisabled      ? null : RunLaneAsync(() => flows.FetchAsync(request, cts.Token));

        var memoryResult = memoryTask is null ? null : await memoryTask;
        SessionStartMemoryContextResult?[] others = [
            guidelinesTask is null ? null : await guidelinesTask,
            flowsTask      is null ? null : await flowsTask
        ];

        return Combine(memoryResult, others);
    }

    async Task<SessionStartMemoryContextResult> RunLaneAsync(Func<Task<SessionStartMemoryContextResult>> lane) {
        try {
            return await lane();
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart context lane skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    static SessionStartMemoryContextResult Combine(SessionStartMemoryContextResult? memoryResult, SessionStartMemoryContextResult?[] others) {
        var memoryFragment = Ready(memoryResult);
        var otherFragments = others.Select(Ready).OfType<string>().ToList();

        if (memoryFragment is not null || otherFragments.Count > 0)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, Compose(memoryFragment, otherFragments));

        SessionStartMemoryContextResult?[] all = [memoryResult, ..others];
        var retries = all.Where(r => r?.Disposition == SessionStartMemoryDisposition.RetryableFailure).ToList();
        if (retries.Count == 0) return SessionStartMemoryContextResult.Empty;

        return new SessionStartMemoryContextResult(
            SessionStartMemoryDisposition.RetryableFailure, RetryAfter: retries.Max(r => r!.RetryAfter));

        static string? Ready(SessionStartMemoryContextResult? r) =>
            r is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } f } ? f : null;
    }

    /// <summary>Marker first: Pi and OpenCode capture stdout only when it opens with it, and only the memory
    /// fragment carries it.</summary>
    static string Compose(string? memoryFragment, IReadOnlyList<string> others) {
        var rest = string.Join("\n\n", others);
        if (memoryFragment is null) return MemoryIndexEmitter.FragmentMarker + "\n" + rest;

        return others.Count == 0 ? memoryFragment : memoryFragment + "\n\n" + rest;
    }

    static bool IsFailOpen(Exception ex) =>
        ex is HttpRequestException or IOException or JsonException or
              OperationCanceledException or UnauthorizedAccessException or InvalidDataException;
}
