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
    /// "Submit" schedule: the delay (relative to the previous write) before each of several
    /// carriage returns sent after a pasted message / a "/exit" command. codex's TUI suppresses
    /// Enter-as-submit for a fixed window right after it ingests a paste, treating a CR in that
    /// window as a literal newline rather than a submit (codex's <c>PASTE_ENTER_SUPPRESS_WINDOW</c>
    /// = 120ms; it is timer-driven and outlives the paste's own flush). A single CR ~50ms behind the
    /// paste lands inside that window and is swallowed, so the message stays unsent (GitHub #349;
    /// codex fails deterministically, claude — a different runtime — intermittently). A CR DOES
    /// submit once it arrives past the window, but the daemon can't know when the reviewer finished
    /// ingesting, so it sends several CRs on an escalating schedule; the later ones land comfortably
    /// past the window regardless of ingest jitter. Once the composer submits, each further CR is a
    /// no-op ONLY in the review launch config (--ask-for-approval never / bypassPermissions — no
    /// prompt is present for a stray Enter to accept); this schedule is not safe to reuse where an
    /// Enter could confirm a dialog. Validated against real codex 0.144 and claude 2.1.218 on
    /// Windows ConPTY (single 50ms CR: codex 0/2, claude 1/3; this schedule: 3/3 each).
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
    /// TUI treats it as one pasted block, then submits it with the carriage-return schedule (see
    /// <see cref="SubmitCarriageReturnSchedule"/>). Without the paste markers a large multi-line
    /// message is mis-handled; without the multi-CR submit the message is left unsent in the
    /// composer (GitHub #349 — a single CR lands inside the reviewer's post-paste Enter-suppression
    /// window). Both hosted CLIs enable bracketed-paste mode, so the markers are consumed as paste
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
    /// Submits the current composer by sending carriage returns on the
    /// <see cref="SubmitCarriageReturnSchedule"/> — see its remarks for why one CR is unreliable.
    /// Shared by <see cref="SendUserInputAsync"/> (a pasted message) and
    /// <see cref="RequestGracefulStopAsync"/> (the "/exit" command), both of which otherwise stall
    /// unsubmitted for codex/claude. Because the schedule spans ~2.4s, the reviewer can exit
    /// mid-way (a "/exit" that took effect, or any exit) and close its PTY input pipe; a later CR
    /// would then write into a closed pipe. <see cref="Pty.IPtyProcess.WriteAsync(string)"/> does
    /// not guard that, and the throw would surface to the caller (e.g. as a spurious graceful-exit
    /// failure), so stop once the process has exited and treat a mid-write pipe-closure as benign.
    /// </summary>
    async Task SubmitAsync() {
        foreach (var delay in SubmitCarriageReturnSchedule) {
            if (pty.HasExited) return; // reviewer already gone — nothing to submit into
            await Task.Delay(delay);
            if (pty.HasExited) return;
            try {
                await pty.WriteAsync("\r");
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                // The reviewer exited between the HasExited check and this write and its input pipe
                // is closing (HasExited may not have flipped yet). A CR into a closed pipe is benign
                // here — the composer we were submitting into is gone — so stop, don't propagate.
                return;
            }
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => pty.DisposeAsync();
}
