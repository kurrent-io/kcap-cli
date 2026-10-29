using System.Runtime.InteropServices;
using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli;

/// <summary>
/// Interrupt and abandonment handling for interactive commands. A blocking Spectre.Console prompt observes neither
/// Ctrl+C nor a closed console, so without this a command whose terminal or launching agent went away mid-prompt
/// stays alive forever. Ctrl+C, SIGTERM and SIGHUP exit 130, and a watchdog exits the same way once the parent
/// process is gone, since a detached pseudo-console delivers no signal at all. Inside a <see cref="Defer"/> scope an
/// interrupt cancels the scope's token instead, and the exit waits until the scope ends. Idempotent.
/// </summary>
static class InteractiveLifetime {
    // Exit code for an interrupted interactive command (128 + SIGINT).
    const int InterruptExitCode = 130;

    // How often the parent-liveness watchdog polls. Short enough to reap a
    // stray promptly, long enough to be negligible overhead.
    static readonly TimeSpan ParentPollInterval = TimeSpan.FromSeconds(3);

    // What an interrupt may spend telling a server the machine has gone. A whole second of an exit
    // the user has already asked for is as much as this is worth: one small POST to a host we are
    // mid-conversation with either lands well inside it or is not going to.
    internal static readonly TimeSpan ExitNoticeBudget = TimeSpan.FromSeconds(1);

    // PosixSignalRegistration.Create returns an IDisposable that owns the
    // underlying handler slot; if it isn't rooted for the process lifetime the
    // finalizer silently unregisters the handler (see WatchCommand). Hold the
    // registrations in static fields so they live until the process exits.
    static IDisposable? _sigterm;
    static IDisposable? _sighup;
    static int          _installed;

    /// <summary>
    /// Commands that drive interactive Spectre.Console prompts and therefore
    /// need the safety net. Kept as an explicit allow-list so non-interactive
    /// commands (hooks, mcp servers) and commands that manage their own
    /// lifetime (<c>watch</c>, <c>daemon</c>) are never affected.
    ///
    /// <c>feedback</c> joins this list for the same reason <c>import</c> does: on a real TTY with
    /// no <c>-m</c> flag it blocks on a plain <see cref="Console.ReadLine"/> loop, which has the
    /// same "no timeout, no signal awareness" gap as a Spectre prompt.
    /// </summary>
    public static bool IsInteractiveCommand(string command) => command is
        "setup" or "login" or "profile" or "use" or "import" or "uninstall" or "feedback" or "local";

    /// <summary>
    /// Installs the interrupt handlers and the parent-liveness watchdog. Best
    /// effort throughout: a failure to register any single guard must never
    /// break the command it is meant to protect.
    /// </summary>
    public static void Install() {
        if (Interlocked.Exchange(ref _installed, 1) != 0) {
            return;
        }

        // Ctrl+C: exit deterministically instead of relying on the default
        // terminate action, which a blocking prompt read can swallow.
        try {
            Console.CancelKeyPress += static (_, e) => {
                e.Cancel = true; // we own termination below
                Interrupt(InterruptSource.Signal);
            };
        } catch {
            // Some hosts don't support CancelKeyPress; the watchdog still covers us.
        }

        // SIGTERM / SIGHUP (Unix): a closed terminal or `kill` should reap us.
        // No-ops on platforms where the signal isn't delivered.
        try {
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, static ctx => {
                ctx.Cancel = true;
                Interrupt(InterruptSource.Signal);
            });
        } catch {
            // best effort
        }

        try {
            _sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, static ctx => {
                ctx.Cancel = true;
                Interrupt(InterruptSource.Signal);
            });
        } catch {
            // best effort
        }

        // Parent-liveness watchdog. On Windows, closing the console window or
        // force-killing the launching agent delivers no signal we can catch
        // from inside a blocking prompt read, so poll the parent instead: once
        // the process that launched us is gone there is no one left to answer
        // the prompt, so self-terminate rather than orphan.
        try {
            StartParentWatchdog(ProcessHelpers.GetParentPid());
        } catch {
            // best effort
        }
    }

    /// <summary>Written raw rather than through Spectre: this runs on the way out of a signal handler,
    /// where taking a renderer's locks is the last thing worth doing. It goes to stderr so a machine
    /// reader of stdout (<c>--json</c>) never sees an escape sequence.</summary>
    static void ShowCursor() =>
        ShowCursor(Console.Error, Console.IsErrorRedirected, Console.Out, Console.IsOutputRedirected);

    /// <summary>Prefers stderr, so <c>--json</c> stdout carries nothing but event lines; falls back to a terminal
    /// stdout when stderr is redirected, since that is where the cursor was hidden.</summary>
    internal static void ShowCursor(TextWriter error, bool errorRedirected, TextWriter output, bool outputRedirected) {
        try {
            if (!errorRedirected) error.Write("\u001b[?25h");
            else if (!outputRedirected) output.Write("\u001b[?25h");
        } catch {
            // A closed or detached stream is exactly the case with no cursor to restore.
        }
    }

    internal enum InterruptSource {
        /// <summary>Ctrl+C, SIGTERM or SIGHUP: a person or supervisor asking us to stop.</summary>
        Signal,

        /// <summary>The parent-liveness poll, which repeats every interval for as long as the parent is gone.</summary>
        Watchdog
    }

    // Test seam; Environment.Exit in production.
    internal static Action<int> Exit = Environment.Exit;

    static readonly object DeferralLock = new();
    static Deferral?       _live;

    /// <summary>
    /// Turns interrupts into a cancelled <see cref="Deferral.Token"/> so a command can roll back and say its
    /// last words; disposing the scope then exits 130 if one arrived. A second signal exits at once, and
    /// watchdog repeats only cancel again. Only one scope may be live.
    /// </summary>
    internal static Deferral Defer() {
        lock (DeferralLock) {
            if (_live is not null) throw new InvalidOperationException("An interrupt deferral is already live.");

            return _live = new Deferral();
        }
    }

    internal sealed class Deferral : IDisposable {
        readonly CancellationTokenSource _cts = new();
        int                              _interrupted;
        int                              _disposed;

        internal Deferral() { }

        public CancellationToken Token => _cts.Token;

        public bool Interrupted => Volatile.Read(ref _interrupted) != 0;

        /// <summary>True when this was the first interrupt received.</summary>
        internal bool MarkInterrupted() => Interlocked.Exchange(ref _interrupted, 1) == 0;

        internal void Cancel() {
            try {
                // Async so the command's rollback never runs on the signal or watchdog thread.
                _ = _cts.CancelAsync().ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            } catch (ObjectDisposedException) {
                // Raced a Dispose that already took the exit.
            }
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            bool exit;
            lock (DeferralLock) {
                if (ReferenceEquals(_live, this)) _live = null;
                exit = Interrupted;
            }

            _cts.Dispose();

            if (exit) ExitNow();
        }
    }

    internal static void Interrupt(InterruptSource source) {
        Deferral? live;
        bool      first = false;

        lock (DeferralLock) {
            live = _live;
            if (live is not null) first = live.MarkInterrupted();
        }

        if (live is not null) {
            live.Cancel();

            if (first || source == InterruptSource.Watchdog) return;
        }

        ExitNow();
    }

    /// <summary>
    /// The exit every interrupt path takes. <c>Environment.Exit</c> runs no <c>finally</c>, so anything a
    /// command needs to say on its way out has to be said here. The browser setup flow uses it to tell the
    /// server this machine has stopped listening, which is what stops the page going on to offer decisions
    /// nobody will act on.
    /// </summary>
    static void ExitNow() {
        // First, and before the notice spends its budget on the network: a live region hides the cursor
        // for the duration of a render and gives it back on dispose, and no exit from here unwinds far
        // enough to dispose one - which leaves the shell with no cursor after an interrupted import or
        // browser wait.
        ShowCursor();

        FirstRunInterruptRelinquish.RunBeforeExit(ExitNoticeBudget);

        Exit(InterruptExitCode);
    }

    static void StartParentWatchdog(int? parentPid) {
        // pid <= 1 means no real parent (init / detached) — nothing sane to
        // monitor, so skip rather than risk self-terminating immediately.
        if (parentPid is not { } ppid || ppid <= 1 || !ProcessHelpers.IsProcessAlive(ppid)) {
            return;
        }

        var thread = new Thread(() => {
            while (true) {
                Thread.Sleep(ParentPollInterval);

                if (!ProcessHelpers.IsProcessAlive(ppid)) {
                    Interrupt(InterruptSource.Watchdog);
                }
            }
        }) {
            IsBackground = true,
            Name         = "kcap-interactive-parent-watchdog"
        };

        thread.Start();
    }
}
