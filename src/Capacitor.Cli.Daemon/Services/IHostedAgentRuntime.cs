namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Vendor-agnostic hosted-agent process abstraction. <see cref="AgentOrchestrator"/> owns the
/// vendor-neutral lifecycle (status flips, terminal fan-out to local sinks, server calls, worktree
/// cleanup, heartbeats); the runtime owns the vendor-specific process and I/O.
///
/// <see cref="PtyHostedAgentRuntime"/> wraps an <see cref="Pty.IPtyProcess"/> for the interactive
/// CLIs (Claude, Codex). <c>AcpHostedAgentRuntime</c> (AI-684) speaks ACP JSON-RPC over stdio for
/// Cursor. The orchestrator only ever touches this interface, never a raw PTY.
/// </summary>
internal interface IHostedAgentRuntime : IAsyncDisposable {
    /// <summary>Vendor token this runtime hosts ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>OS process id of the hosted agent (for logging).</summary>
    int Pid { get; }

    /// <summary>True once the hosted process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>
    /// True when <see cref="ReadOutputAsync"/> yields real terminal bytes that the orchestrator's
    /// read loop can use as a liveness/lifecycle signal — <c>true</c> for <see cref="PtyHostedAgentRuntime"/>
    /// (Claude/Codex), <c>false</c> for the ACP runtime (its stdout is protocol traffic, never
    /// terminal output, so <see cref="ReadOutputAsync"/> never yields any bytes at all).
    /// <see cref="AgentOrchestrator"/> uses this to pick between two lifecycle strategies (PR #244
    /// review, Fix B/E): a PTY runtime's Starting→Running flip and startup-failure heuristic both
    /// key off the FIRST output chunk / the output stream ending, which never happens for a
    /// no-terminal ACP runtime — without this flag an ACP agent would flip Running only once it
    /// exits (i.e. never while live) and get misclassified as a startup failure on every normal
    /// exit.
    /// </summary>
    bool EmitsTerminalOutput { get; }

    /// <summary>
    /// Terminal byte stream consumed by the orchestrator read loop. PTY runtimes yield real
    /// terminal output; the ACP runtime yields an empty stream until AI-687 adds a terminal
    /// capability (ACP stdout is protocol traffic, never terminal output).
    /// </summary>
    IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default);

    /// <summary>
    /// Hosted-UI text input (server <c>SendInput</c>). PTY runtimes perform the CLI-specific
    /// text-then-Enter split write; the ACP runtime sends a <c>session/prompt</c>.
    /// </summary>
    Task SendUserInputAsync(string text);

    /// <summary>
    /// Hosted-UI special key (server <c>SendSpecialKey</c>). PTY runtimes translate via
    /// <see cref="SpecialKeyMap"/> and write the bytes; the ACP runtime maps or ignores.
    /// </summary>
    Task SendSpecialKeyAsync(string key);

    /// <summary>
    /// Raw byte input from a local-attached terminal (`kcap attach` Stdin frames). PTY runtimes
    /// write the bytes verbatim. Local attach is a PTY-only surface, so the ACP runtime throws
    /// <see cref="NotSupportedException"/>; the caller guards on <see cref="Vendor"/>.
    /// </summary>
    Task SendRawInputAsync(byte[] data);

    /// <summary>Viewer resize. PTY runtimes resize the winsize; the ACP runtime no-ops until
    /// AI-687 adds a terminal capability.</summary>
    void Resize(ushort cols, ushort rows);

    /// <summary>
    /// Request a graceful stop <b>before</b> <see cref="TerminateAsync"/>. PTY runtimes send the
    /// CLI's "/exit" so it can fire its own session-end hook; the ACP runtime sends
    /// <c>session/cancel</c>. Best-effort — the orchestrator falls through to terminate.
    /// </summary>
    Task RequestGracefulStopAsync();

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
