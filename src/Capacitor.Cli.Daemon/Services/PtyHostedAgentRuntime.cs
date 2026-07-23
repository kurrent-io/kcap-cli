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
    /// Escalating "submit" schedule: the delay (relative to the previous write) before each of a
    /// series of carriage returns sent after a message/command. A SINGLE CR ~50ms after a
    /// bracketed paste is unreliably folded into paste-finalization instead of submitting the
    /// composer — codex leaves the message unsent every time, claude intermittently (GitHub #349).
    /// The reviewer submits reliably only when a CR arrives as a distinct keystroke AFTER it has
    /// finished ingesting the paste, and that ingest time varies (a longer single delay does not
    /// fix it — the CR must land as its own input event), so we send several CRs spread over an
    /// escalating window; once the composer has submitted, every further CR is a harmless
    /// empty-composer no-op. Validated against real codex 0.144 and claude 2.1.218 on Windows
    /// ConPTY (3/3 each with this schedule; the old single 50ms CR was 0/2 and 1/3 respectively).
    /// </summary>
    internal static readonly TimeSpan[] SubmitCarriageReturnSchedule = [
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(1200),
    ];

    public string  Vendor              => vendor;
    public int     Pid                 => pty.Pid;
    public bool    HasExited           => pty.HasExited;
    public int?    ExitCode            => pty.ExitCode;
    public string? StartIdentity       => pty.StartIdentity;
    public bool    EmitsTerminalOutput => true;

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => pty.ReadOutputAsync(ct);

    /// <summary>
    /// Delivers <paramref name="text"/> as a bracketed paste (ESC[200~ … ESC[201~) so the agent's
    /// TUI treats it as one pasted block, then submits it with the escalating carriage-return
    /// schedule (see <see cref="SubmitCarriageReturnSchedule"/>). Without the paste markers a
    /// large multi-line message is mis-handled; without the multi-CR submit the message is left
    /// unsent in the composer (GitHub #349 — the single CR is folded into paste-finalization).
    /// Both hosted CLIs enable bracketed-paste mode, so the markers are consumed as paste
    /// delimiters (not echoed).
    /// </summary>
    public async Task SendUserInputAsync(string text) {
        await pty.WriteAsync($"\x1b[200~{text}\x1b[201~");
        await SubmitAsync();
    }

    public Task SendSpecialKeyAsync(string key) {
        var bytes = SpecialKeyMap.ToBytes(key);

        return bytes.Length > 0 ? pty.WriteAsync(bytes) : Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) => pty.WriteAsync(data);

    public void Resize(ushort cols, ushort rows) => pty.Resize(cols, rows);

    public async Task RequestGracefulStopAsync() {
        await pty.WriteAsync("/exit");
        await SubmitAsync();
    }

    /// <summary>
    /// Submits the current composer by sending carriage returns on the escalating
    /// <see cref="SubmitCarriageReturnSchedule"/> — see its remarks for why a single CR is
    /// unreliable and why the extra CRs are safe (empty-composer no-ops once submitted). Shared by
    /// <see cref="SendUserInputAsync"/> (a pasted message) and <see cref="RequestGracefulStopAsync"/>
    /// (the "/exit" command), both of which otherwise stall unsubmitted for codex/claude.
    /// </summary>
    async Task SubmitAsync() {
        foreach (var delay in SubmitCarriageReturnSchedule) {
            await Task.Delay(delay);
            await pty.WriteAsync("\r");
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => pty.DisposeAsync();
}
