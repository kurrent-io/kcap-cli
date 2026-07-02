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

    public string Vendor    => vendor;
    public int    Pid       => pty.Pid;
    public bool   HasExited => pty.HasExited;
    public int?   ExitCode  => pty.ExitCode;

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => pty.ReadOutputAsync(ct);

    public async Task SendUserInputAsync(string text) {
        await pty.WriteAsync(text);
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
