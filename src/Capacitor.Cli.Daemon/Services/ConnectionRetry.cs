namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Retries a hub invocation across transient connection drops. Used by
/// <see cref="ServerConnection.RequestPermissionAsync"/> so a SignalR blip
/// during a (potentially hours-long) permission wait doesn't surface to the
/// caller as a failure — and therefore doesn't get turned into a silent deny.
///
/// A failure is treated as a transient disconnect (→ wait for readiness, retry)
/// when it is an <see cref="OperationCanceledException"/> (SignalR cancels
/// in-flight invocations when the transport drops) or an
/// <see cref="InvalidOperationException"/> raised while the connection is NOT
/// ready (hub down / re-registering). An <see cref="InvalidOperationException"/>
/// raised while the connection IS ready signals a permanent client/protocol
/// fault that retrying cannot recover, so it propagates — mirroring the
/// <see cref="TerminalOutputSender"/> "connected yet throwing → don't spin"
/// safety valve. Every other exception (e.g. <c>HubException</c>) propagates.
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
            ct.ThrowIfCancellationRequested();

            try {
                return await invoke();
            } catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDisconnect(ex, isReady)) {
                onRetry(attempt);

                // Delay once unconditionally: it covers the race where the
                // connection already recovered by the time we caught (the wait
                // loop would exit immediately) and guarantees the loop can never
                // spin hot.
                await Task.Delay(pollInterval, ct);

                while (!ct.IsCancellationRequested && !isReady())
                    await Task.Delay(pollInterval, ct);
            }
        }
    }

    static bool IsTransientDisconnect(Exception ex, Func<bool> isReady) =>
        ex switch {
            OperationCanceledException => true,
            InvalidOperationException  => !isReady(),
            _                          => false
        };
}
