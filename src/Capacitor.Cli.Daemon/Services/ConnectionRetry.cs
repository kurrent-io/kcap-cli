namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Retries a hub invocation across transient connection drops. Used by
/// <see cref="ServerConnection.RequestPermissionAsync"/> so a SignalR blip
/// during a (potentially hours-long) permission wait doesn't surface to the
/// caller as a failure — and therefore doesn't get turned into a silent deny.
///
/// Every attempt — including the first — waits until the connection is ready
/// (Connected AND re-registered) before invoking, so a request that arrives
/// mid-reconnect can't fire against a connection the server hasn't registered.
/// Because of that gating, a failure is a transient disconnect when it is an
/// <see cref="OperationCanceledException"/> (SignalR cancels in-flight
/// invocations when the transport drops) or an
/// <see cref="InvalidOperationException"/> ("connection is not active" — the
/// transport went down in the gap between the readiness check and the call).
/// Both are retried. Every other exception — <c>HubException</c> (server
/// rejected) or anything unrecognized — propagates to the caller (→ the bridge
/// denies).
///
/// The loop is bounded only by <paramref name="ct"/> (daemon shutdown). The
/// caller's shutdown token is excluded from the transient classification, so a
/// shutdown <see cref="OperationCanceledException"/> propagates rather than
/// being retried.
/// </summary>
internal static class ConnectionRetry {
    public static async Task<T> InvokeWithConnectionRetryAsync<T>(
            Func<Task<T>>     invoke,
            Func<bool>        isReady,
            TimeSpan          pollInterval,
            Action<int>       onRetry,
            CancellationToken ct
        ) {
        for (var attempt = 1; ; attempt++) {
            // Wait for readiness before EVERY attempt, including the first. A
            // permission request can arrive while the daemon is mid-reconnect or
            // re-registering; invoking then could hit a connection the server
            // hasn't registered and surface as a HubException — a spurious deny.
            // Gating all attempts on readiness (not just post-failure retries)
            // closes that race.
            while (!ct.IsCancellationRequested && !isReady())
                await Task.Delay(pollInterval, ct);

            ct.ThrowIfCancellationRequested();

            try {
                return await invoke();
            } catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDisconnect(ex)) {
                onRetry(attempt);

                // Brief delay before looping back to the readiness wait, so the
                // loop can never spin hot even if isReady() flips true instantly.
                await Task.Delay(pollInterval, ct);
            }
        }
    }

    // We only ever invoke once the connection was observed ready, so an
    // InvalidOperationException ("connection is not active") here means the
    // transport dropped between the readiness check and the call — transient,
    // like the OperationCanceledException SignalR raises for an in-flight
    // invocation killed by a transport loss. HubException and any other
    // exception are not transient and propagate.
    static bool IsTransientDisconnect(Exception ex) =>
        ex is OperationCanceledException or InvalidOperationException;
}
