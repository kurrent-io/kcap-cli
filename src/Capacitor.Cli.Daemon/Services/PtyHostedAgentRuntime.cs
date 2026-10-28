using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
///
/// <paramref name="readyOnBracketedPaste"/> makes the launch prompt wait for the TUI to enable
/// bracketed paste rather than for any output. The factory sets it on Windows, where ConPTY writes
/// its own preamble before the agent starts; see <see cref="DeliverLaunchPrompt"/>.
/// </summary>
internal sealed partial class PtyHostedAgentRuntime(
        string vendor, IPtyProcess pty, TimeProvider time, bool approvalsDisabled = false,
        ILogger? logger = null, bool readyOnBracketedPaste = false) : IHostedAgentRuntime {
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
    internal static readonly TimeSpan SingleSubmitDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the graceful stop waits for the input lane before writing without it. A delivery
    /// parked in an uncancellable <c>write(2)</c> holds the lane until the terminate this stop
    /// precedes, so an unbounded wait here would hold the stop past the reviewer reap's bound.
    /// </summary>
    internal static readonly TimeSpan GracefulStopLaneWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long output must have been silent, once the TUI looks ready, before the launch prompt is
    /// pasted: a TUI still drawing its banner or startup spinner is not reading its composer yet.
    /// Codex's spinner redraws every 100 ms, so the window has to outlast that gap.
    /// </summary>
    internal static readonly TimeSpan LaunchPromptQuietWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on that wait: a TUI that never settles still gets its prompt.</summary>
    internal static readonly TimeSpan LaunchPromptMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>The PTY offers no exit event and the TUI can turn bracketed paste off again, so the
    /// launch prompt's waits are sliced and both re-read between slices.</summary>
    static readonly TimeSpan LaunchPromptPoll = TimeSpan.FromMilliseconds(250);

    readonly TerminalModeTracker _modes = new();
    readonly ILogger _logger = logger ?? NullLogger.Instance;
    readonly SemaphoreSlim           _lane        = new(1, 1);
    readonly CancellationTokenSource _disposal    = new();
    readonly Lock                    _dialogGate  = new();
    long                             _lastOutputAt;
    volatile bool                    _sawOutput;
    volatile bool                    _stopRequested;
    bool                             _deliveryHoldsLane;
    TaskCompletionSource?            _dialogGone;

    /// <summary>The pending launch-prompt delivery, or null when the launch carried no prompt to paste.</summary>
    internal Task<LaunchPromptOutcome>? LaunchPromptDelivery { get; private set; }

    public string  Vendor              => vendor;
    public int     Pid                 => pty.Pid;
    public bool    HasExited           => pty.HasExited;
    public int?    ExitCode            => pty.ExitCode;
    public string? StartIdentity       => pty.StartIdentity;
    public bool    EmitsTerminalOutput => true;

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        await foreach (var chunk in pty.ReadOutputAsync(ct).ConfigureAwait(false)) {
            // Stamped first: a reader that sees bracketed paste turn on must also see this chunk's time.
            Volatile.Write(ref _lastOutputAt, time.GetTimestamp());
            _modes.Feed(chunk);
            _sawOutput = true;

            yield return chunk;
        }
    }

    bool LooksReadyForInput => readyOnBracketedPaste ? _modes.IsBracketedPasteEnabled : _sawOutput;

    public void NoteTerminalDialog(bool showing) {
        lock (_dialogGate) {
            if (showing) {
                _dialogGone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            } else if (_dialogGone is { } gone) {
                _dialogGone = null;
                gone.TrySetResult();
            }
        }
    }

    Task? DialogGone {
        get { lock (_dialogGate) { return _dialogGone?.Task; } }
    }

    /// <summary>
    /// Makes <paramref name="prompt"/> the agent's first message without putting it on the command
    /// line: pastes it once the TUI looks ready and output has then been quiet for
    /// <see cref="LaunchPromptQuietWindow"/>, or <see cref="LaunchPromptMaxWait"/> has passed. A text
    /// follow-up sent meanwhile lands after it; raw and special keys do not wait, so a viewer can
    /// answer a startup dialog themselves. A dialog on screen is not a settled startup and has no
    /// time limit: its default option would take the submit Enter and leave the prompt unsent in the
    /// composer.
    /// </summary>
    /// <remarks>
    /// Under ConPTY output alone is no sign of readiness: it writes its own preamble before the agent
    /// starts, and a paste sent while the agent is still booting reaches its composer but the
    /// submitting CR is lost, leaving the prompt unsent.
    /// </remarks>
    internal void DeliverLaunchPrompt(string prompt) {
        if (LaunchPromptDelivery is not null) throw new InvalidOperationException("The launch prompt is already queued.");

        LaunchPromptDelivery = DeliverLaunchPromptAsync(prompt);
    }

    async Task<LaunchPromptOutcome> DeliverLaunchPromptAsync(string prompt) {
        try {
            bool settled;

            // A dialog can open while the delivery waits for the lane, so it is checked again once the
            // lane is held, and the wait starts over.
            while (true) {
                settled = await WaitForStartupToSettleAsync(_disposal.Token);
                if (pty.HasExited) return LaunchPromptOutcome.AgentExited;
                if (_stopRequested) return LaunchPromptOutcome.StopRequested;

                await _lane.WaitAsync(_disposal.Token);
                if (DialogGone is null) break;

                _lane.Release();
            }

            _deliveryHoldsLane = true;

            try {
                if (_stopRequested) return LaunchPromptOutcome.StopRequested;

                await pty.WriteAsync(BracketedPaste.Frame(prompt));
                if (!await SubmitAsync(WaitOutDialogsAsync)) return LaunchPromptOutcome.StopRequested;
            } finally {
                if (_deliveryHoldsLane) {
                    _deliveryHoldsLane = false;
                    try { _lane.Release(); } catch (ObjectDisposedException) { }
                }
            }

            return settled ? LaunchPromptOutcome.Delivered : LaunchPromptOutcome.DeliveredAfterMaxWait;
        } catch (OperationCanceledException) {
            return LaunchPromptOutcome.Cancelled;
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
            return LaunchPromptOutcome.WriteFailed;
        }
    }

    /// <summary>A follow-up waits for the launch prompt so it lands after it. The delivery reports
    /// its own fate; the follow-up only needs it to be over.</summary>
    async Task WaitForLaunchPromptAsync() {
        if (LaunchPromptDelivery is not { } delivery) return;

        try { await delivery; } catch (Exception) { }
    }

    /// <summary>True once output has been quiet for the window, false when the wait expired first.
    /// Returns early, true, when the agent has exited or been asked to stop; the caller checks those.</summary>
    async Task<bool> WaitForStartupToSettleAsync(CancellationToken ct) {
        var started = time.GetTimestamp();

        while (!pty.HasExited && !_stopRequested) {
            if (DialogGone is not null) {
                await WaitWhileDialogShowsAsync(ct);
                started = time.GetTimestamp();

                continue;
            }

            var remaining = LaunchPromptMaxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;

            if (!LooksReadyForInput) {
                await Task.Delay(remaining < LaunchPromptPoll ? remaining : LaunchPromptPoll, time, ct);

                continue;
            }

            var quietFor = time.GetElapsedTime(Volatile.Read(ref _lastOutputAt));
            if (quietFor >= LaunchPromptQuietWindow) return true;

            await Task.Delay(LaunchPromptQuietWindow - quietFor, time, ct);
        }

        return true;
    }

    /// <summary>Called by the launch delivery holding the input lane, before each submit Enter. A dialog
    /// that came up after the paste would take the Enter, and answering it takes the lane, so each one is
    /// waited out with the lane released. Returns holding the lane; false when a stop was asked for.</summary>
    async Task<bool> WaitOutDialogsAsync() {
        while (DialogGone is not null && !pty.HasExited && !_stopRequested) {
            _lane.Release();
            _deliveryHoldsLane = false;
            await WaitWhileDialogShowsAsync(_disposal.Token);
            await _lane.WaitAsync(_disposal.Token);
            _deliveryHoldsLane = true;
        }

        return !_stopRequested;
    }

    /// <summary>Returns once no dialog is on screen, or the agent has exited or been asked to stop.</summary>
    async Task WaitWhileDialogShowsAsync(CancellationToken ct) {
        while (DialogGone is { } gone && !pty.HasExited && !_stopRequested) {
            try {
                await gone.WaitAsync(LaunchPromptPoll, time, ct);
            } catch (TimeoutException) { }
        }
    }

    /// <summary>
    /// Delivers <paramref name="text"/> as a bracketed paste (ESC[200~ … ESC[201~) so the TUI treats
    /// it as one block, then submits it (see <see cref="SubmitAsync"/>).
    /// </summary>
    public async Task SendUserInputAsync(string text) {
        await WaitForLaunchPromptAsync();
        await _lane.WaitAsync();

        try {
            await pty.WriteAsync(BracketedPaste.Frame(text));
            await SubmitAsync();
        } finally {
            _lane.Release();
        }
    }

    public async Task SendSpecialKeyAsync(string key) {
        var isWheel = TerminalScrollKey.TryDecode(key, out var bytes);
        if (!isWheel) bytes = SpecialKeyMap.ToBytes(key);
        if (bytes.Length == 0) return;

        await _lane.WaitAsync();

        try {
            if (isWheel && (!_modes.IsMouseReporting || pty.HasExited)) {
                LogWheelDropped(_logger);
                return;
            }
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
        _stopRequested = true;
        var holdsLane  = await _lane.WaitAsync(GracefulStopLaneWait, CancellationToken.None);

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
    /// <param name="beforeCarriageReturn">Runs before each CR; false withholds it and the rest.</param>
    /// <returns>False when <paramref name="beforeCarriageReturn"/> withheld a CR.</returns>
    async Task<bool> SubmitAsync(Func<Task<bool>>? beforeCarriageReturn = null) {
        if (approvalsDisabled) {
            foreach (var delay in SubmitCarriageReturnSchedule) {
                if (pty.HasExited) return true;
                await Task.Delay(delay, time);
                if (beforeCarriageReturn is not null && !await beforeCarriageReturn()) return false;
                if (!await WriteSubmitCarriageReturnAsync()) return true; // stop once the reviewer is gone
            }

            return true;
        }

        await Task.Delay(SingleSubmitDelay, time);
        if (beforeCarriageReturn is not null && !await beforeCarriageReturn()) return false;
        await WriteSubmitCarriageReturnAsync();

        return true;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped wheel input while PTY mouse reporting is inactive or the process has exited")]
    static partial void LogWheelDropped(ILogger logger);

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public async ValueTask DisposeAsync() {
        await _disposal.CancelAsync();
        await pty.DisposeAsync();
        _lane.Dispose();
        _disposal.Dispose();
    }
}
