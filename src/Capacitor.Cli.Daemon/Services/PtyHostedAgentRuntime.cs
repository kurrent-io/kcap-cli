using System.Runtime.CompilerServices;
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

    /// <summary>
    /// How long startup output must have been silent before the launch prompt is pasted: a TUI still
    /// drawing its banner or startup spinner is not reading its composer yet. Codex's spinner redraws
    /// every 100 ms, so the window has to outlast that gap.
    /// </summary>
    internal static readonly TimeSpan LaunchPromptQuietWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on that wait: a TUI that never settles still gets its prompt.</summary>
    internal static readonly TimeSpan LaunchPromptMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>The PTY offers no exit event, so the wait for first output is sliced and the exit
    /// flag re-read between slices.</summary>
    static readonly TimeSpan LaunchPromptExitPoll = TimeSpan.FromMilliseconds(250);

    readonly SemaphoreSlim           _lane        = new(1, 1);
    readonly TaskCompletionSource    _firstOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _disposal    = new();
    long                             _lastOutputAt;

    /// <summary>The pending launch-prompt delivery, or null when the launch carried no prompt to paste.</summary>
    internal Task? LaunchPromptDelivery { get; private set; }

    public string  Vendor              => vendor;
    public int     Pid                 => pty.Pid;
    public bool    HasExited           => pty.HasExited;
    public int?    ExitCode            => pty.ExitCode;
    public string? StartIdentity       => pty.StartIdentity;
    public bool    EmitsTerminalOutput => true;

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        await foreach (var chunk in pty.ReadOutputAsync(ct).ConfigureAwait(false)) {
            Volatile.Write(ref _lastOutputAt, time.GetTimestamp());
            _firstOutput.TrySetResult();

            yield return chunk;
        }
    }

    /// <summary>
    /// Makes <paramref name="prompt"/> the agent's first message without putting it on the command
    /// line. Takes the input lane at once, so a follow-up sent during startup queues behind the prompt,
    /// and pastes once startup output has been quiet for <see cref="LaunchPromptQuietWindow"/> or
    /// <see cref="LaunchPromptMaxWait"/> has passed. Must run before any other input is offered.
    /// </summary>
    internal void DeliverLaunchPrompt(string prompt) {
        if (!_lane.Wait(0)) throw new InvalidOperationException("The launch prompt must be queued before any other input.");

        LaunchPromptDelivery = DeliverLaunchPromptAsync(prompt);
    }

    async Task DeliverLaunchPromptAsync(string prompt) {
        try {
            await WaitForStartupToSettleAsync(_disposal.Token);
            if (pty.HasExited) return;

            await pty.WriteAsync($"\x1b[200~{prompt}\x1b[201~");
            await SubmitAsync();
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) {
            // The agent is gone or going: there is no composer left to paste into.
        } finally {
            try { _lane.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Returns once output has been quiet for the window, the wait has expired, or the
    /// agent has exited.</summary>
    async Task WaitForStartupToSettleAsync(CancellationToken ct) {
        var started = time.GetTimestamp();

        while (!pty.HasExited) {
            var remaining = LaunchPromptMaxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;

            if (!_firstOutput.Task.IsCompleted) {
                try {
                    await _firstOutput.Task.WaitAsync(remaining < LaunchPromptExitPoll ? remaining : LaunchPromptExitPoll, time, ct);
                } catch (TimeoutException) { }

                continue;
            }

            var quietFor = time.GetElapsedTime(Volatile.Read(ref _lastOutputAt));
            if (quietFor >= LaunchPromptQuietWindow) return;

            await Task.Delay(LaunchPromptQuietWindow - quietFor, time, ct);
        }
    }

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
        await _disposal.CancelAsync();
        await pty.DisposeAsync();
        _lane.Dispose();
        _disposal.Dispose();
    }
}
