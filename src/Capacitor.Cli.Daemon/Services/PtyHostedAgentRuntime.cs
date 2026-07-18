using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> backed by an interactive PTY (Claude, Codex). Wraps an
/// <see cref="IPtyProcess"/> and folds in the CLI-specific input semantics that previously lived
/// inline in <see cref="AgentOrchestrator"/>: the text-then-Enter split write, the
/// <see cref="SpecialKeyMap"/> translation, and the graceful "/exit" stop.
/// </summary>
internal sealed class PtyHostedAgentRuntime(string vendor, IPtyProcess pty) : IHostedAgentRuntime {
    /// <summary>
    /// Claude/Codex CLIs require the command text and the Enter key to arrive as SEPARATE PTY
    /// writes with a small delay between them; a single combined write makes the CLI treat the
    /// carriage return as part of the command buffer instead of submitting it. Moved verbatim
    /// from AgentOrchestrator.HandleSendInput / HandleStopAgent.
    /// </summary>
    internal static readonly TimeSpan InputSubmitDelay = TimeSpan.FromMilliseconds(50);

    public string Vendor              => vendor;
    public int    Pid                 => pty.Pid;
    public bool   HasExited           => pty.HasExited;
    public int?   ExitCode            => pty.ExitCode;
    public bool   EmitsTerminalOutput => true;

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => pty.ReadOutputAsync(ct);

    /// <summary>
    /// Delivers <paramref name="text"/> as a bracketed paste (ESC[200~ … ESC[201~) so the agent's
    /// TUI treats it as one pasted block and the following Enter is an unambiguous submit
    /// keypress. Without the paste markers a large multi-line message is mis-handled:
    /// Codex never submits it at all, and Claude only submits it ~50% of the time — the CR races
    /// the still-ingesting paste and is folded in as a literal newline, so the text sits in the
    /// composer until a later, isolated keystroke finishes it. Both hosted CLIs enable
    /// bracketed-paste mode, so the markers are consumed as paste delimiters (not echoed). Keep
    /// the text and the Enter as separate writes with a short delay so the CR lands as a distinct
    /// PTY read after the paste (Claude also needs the CR split out, or it treats the carriage
    /// return as part of the buffer rather than a submit).
    /// </summary>
    public async Task SendUserInputAsync(string text) {
        await pty.WriteAsync($"\x1b[200~{text}\x1b[201~");
        await Task.Delay(InputSubmitDelay);
        await pty.WriteAsync("\r");
    }

    public Task SendSpecialKeyAsync(string key) {
        var bytes = SpecialKeyMap.ToBytes(key);

        return bytes.Length > 0 ? pty.WriteAsync(bytes) : Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) => pty.WriteAsync(data);

    public void Resize(ushort cols, ushort rows) => pty.Resize(cols, rows);

    public async Task RequestGracefulStopAsync() {
        await pty.WriteAsync("/exit");
        await Task.Delay(InputSubmitDelay);
        await pty.WriteAsync("\r");
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => pty.DisposeAsync();
}
