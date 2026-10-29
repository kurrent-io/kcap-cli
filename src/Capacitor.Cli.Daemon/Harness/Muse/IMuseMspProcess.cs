namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// The long-lived <c>muse serve</c> child speaking MSP JSON-RPC over stdio: one process backs a
/// hosted session and its stdin stays open for that whole lifetime.
///
/// <para>Contracts a real implementation honors:</para>
/// <para>0. <see cref="IAsyncDisposable.DisposeAsync"/> may kill a running child, so it is not exit
/// evidence: terminate explicitly and read <see cref="HasExited"/> before disposing.</para>
/// <para>1. Dispose is idempotent.</para>
/// <para>2. <see cref="TerminateAsync"/> is safe after dispose.</para>
/// <para>3. <see cref="WriteLineAsync"/> calls are serialized, so concurrent writers never tear a
/// line on stdin.</para>
/// </summary>
internal interface IMuseMspProcess : IAsyncDisposable {
    int Pid { get; }

    bool HasExited { get; }

    /// <summary>Null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>A bounded capture of the child's stderr, or null if it wrote nothing.</summary>
    string? Diagnostics { get; }

    /// <summary>The child's stdout, one line at a time, ending at EOF without throwing; throws
    /// <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled first.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>Writes <paramref name="json"/> plus <c>\n</c> to stdin and flushes before returning.</summary>
    Task WriteLineAsync(string json, CancellationToken ct);

    /// <summary>Waits up to <paramref name="timeout"/> for exit; returns silently on timeout.</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Closes stdin, bounded by <paramref name="timeout"/> when a write holds it; a no-op once closed.</summary>
    Task CloseInputAsync(TimeSpan timeout);

    /// <summary>Kills the whole process tree immediately, then waits up to <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
