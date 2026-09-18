using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> backed by an interactive PTY (Claude, Codex). Wraps an
/// <see cref="IPtyProcess"/> and folds in the CLI-specific input semantics: bracketed-paste input,
/// the <see cref="SpecialKeyMap"/> translation, and the graceful "/exit" stop.
///
/// <paramref name="approvalsDisabled"/> is set by the factory when the launch turned off interactive
/// approval/permission prompts (codex <c>--ask-for-approval never</c> / claude
/// <c>--permission-mode bypassPermissions</c>), so no dialog an Enter could accept can appear. It
/// gates the submit strategy — see <see cref="SubmitAsync"/>. Defaults to <c>false</c> (assume a
/// prompt could be present), the fail-safe choice for interactive/local launches.
///
/// Input writes take the input lane, so a paste and its submit are never interleaved by another
/// writer; the graceful stop only tries for it (see <see cref="RequestGracefulStopAsync"/>).
/// </summary>
internal sealed class PtyHostedAgentRuntime(
        string vendor, IPtyProcess pty, TimeProvider time, bool approvalsDisabled = false) : IHostedAgentRuntime {
    /// <summary>
    /// Delays (relative to the previous write) before each carriage return on the spray submit path.
    /// A single CR right after a paste is unreliable: codex's TUI suppresses Enter-as-submit for a
    /// fixed window after ingesting a paste (its <c>PASTE_ENTER_SUPPRESS_WINDOW</c> = 120ms,
    /// timer-driven), treating a CR inside it as a newline. The later CRs here land past the window.
    /// </summary>
    internal static readonly TimeSpan[] SubmitCarriageReturnSchedule = [
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(1200),
    ];

    /// <summary>
    /// Past codex's 120ms post-paste Enter-suppression window, so the one CR still submits.
    /// </summary>
    static readonly TimeSpan SingleSubmitDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the graceful stop waits for the input lane before writing without it. A delivery
    /// parked in an uncancellable <c>write(2)</c> holds the lane until the terminate this stop
    /// precedes, so an unbounded wait here would hold the stop past the reviewer reap's bound.
    /// </summary>
    internal static readonly TimeSpan GracefulStopLaneWait = TimeSpan.FromSeconds(1);

    readonly SemaphoreSlim  _lane = new(1, 1);

    public string  Vendor              => vendor;
    public int     Pid                 => pty.Pid;
    public bool    HasExited           => pty.HasExited;
    public int?    ExitCode            => pty.ExitCode;
    public string? StartIdentity       => pty.StartIdentity;
    public bool    EmitsTerminalOutput => true;

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => pty.ReadOutputAsync(ct);

    /// <summary>
    /// Delivers <paramref name="text"/> as a bracketed paste (ESC[200~ … ESC[201~) so the TUI treats
    /// it as one block, then submits it (see <see cref="SubmitAsync"/>).
    /// </summary>
    public async Task SendUserInputAsync(string text) {
        await _lane.WaitAsync();

        try {
            await pty.WriteAsync($"\x1b[200~{text}\x1b[201~");
            await SubmitAsync();
        } finally {
            _lane.Release();
        }
    }

    public async Task SendSpecialKeyAsync(string key) {
        var bytes = SpecialKeyMap.ToBytes(key);
        if (bytes.Length == 0) return;

        await _lane.WaitAsync();

        try {
            await pty.WriteAsync(bytes);
        } finally {
            _lane.Release();
        }
    }

    public async Task SendRawInputAsync(byte[] data) {
        await _lane.WaitAsync();

        try {
            await pty.WriteAsync(data);
        } finally {
            _lane.Release();
        }
    }

    public void Resize(ushort cols, ushort rows) => pty.Resize(cols, rows);

    /// <summary>
    /// Writes "/exit" and submits it. Takes the input lane when it can, but gives up after
    /// <see cref="GracefulStopLaneWait"/> and writes anyway: on a real PTY both writers share the
    /// master fd regardless, and the lane must never be what keeps the stop from being asked for.
    /// </summary>
    public async Task RequestGracefulStopAsync() {
        var holdsLane = await _lane.WaitAsync(GracefulStopLaneWait, CancellationToken.None);

        try {
            await pty.WriteAsync("/exit");
            await SubmitAsync();
        } finally {
            if (holdsLane) _lane.Release();
        }
    }

    /// <summary>
    /// Submits the composer. When <c>approvalsDisabled</c> (no dialog an Enter could accept),
    /// sprays carriage returns on <see cref="SubmitCarriageReturnSchedule"/> so at least one lands
    /// past codex's post-paste Enter-suppression window; the extra CRs are then empty-composer
    /// no-ops. Otherwise sends a single CR — in an interactive session a stray Enter must not
    /// answer a live approval prompt.
    /// </summary>
    async Task SubmitAsync() {
        if (approvalsDisabled) {
            foreach (var delay in SubmitCarriageReturnSchedule) {
                if (pty.HasExited) return;
                await Task.Delay(delay, time);
                if (!await WriteSubmitCarriageReturnAsync()) return; // stop once the reviewer is gone
            }

            return;
        }

        await Task.Delay(SingleSubmitDelay, time);
        await WriteSubmitCarriageReturnAsync();
    }

    /// <summary>
    /// Writes one submit CR. Returns <c>false</c> if the process has already exited or its input pipe
    /// closed mid-write — a benign post-exit write (<see cref="Pty.IPtyProcess.WriteAsync(string)"/>
    /// is unguarded and throws on a closed pipe), which must not propagate as a graceful-exit failure.
    /// </summary>
    async Task<bool> WriteSubmitCarriageReturnAsync() {
        if (pty.HasExited) return false;

        try {
            await pty.WriteAsync("\r");

            return true;
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
            return false;
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public async ValueTask DisposeAsync() {
        await pty.DisposeAsync();
        _lane.Dispose();
    }
}
