using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// Owns the one `kcap setup --local` child for the app's lifetime, beyond the wizard page that
/// started it. Lines and the settled outcome reach the UI through <c>post</c>, in order, so the
/// outcome is always decided after every line the child printed has been applied.
/// </summary>
public sealed class LocalInstallController(
        Func<IKcapCli> cli, LocalRoot root, Func<bool> committedLocal, Action<Action> post, SetupFunnel funnel)
    : ReactiveObject {
    LocalSetupProgress    _progress = new();
    bool                  _running;
    LocalInstallOutcome?  _outcome;
    CancellationTokenSource? _interrupt;
    TaskCompletionSource  _exited = CompletedSource();

    public LocalSetupProgress Progress {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool Running {
        get => _running;
        private set => this.RaiseAndSetIfChanged(ref _running, value);
    }

    public LocalInstallOutcome? Outcome {
        get => _outcome;
        private set => this.RaiseAndSetIfChanged(ref _outcome, value);
    }

    /// Raised once per run, through <c>post</c>, with the durable state the outcome was decided from,
    /// before <see cref="Outcome"/> and <see cref="Running"/> change: anything restated by those
    /// changes reads state that is already current.
    public event Action<LocalSetupSnapshot>? Settling;

    /// Raised once per run, through <c>post</c>, after <see cref="Outcome"/> is set.
    public event Action<LocalInstallOutcome>? Settled;

    /// What the last run was told about search by meaning; a retry tells the next one the same.
    public LocalEmbeddingChoice? Embeddings { get; private set; }

    /// Spawns the child and returns; a no-op while a run is in flight.
    public Task StartAsync(LocalEmbeddingChoice embeddings, CancellationToken ct) {
        if (Running) return Task.CompletedTask;

        Embeddings = embeddings;
        var progress = new LocalSetupProgress();
        var interrupt = new CancellationTokenSource();
        _interrupt = interrupt;
        _exited    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress   = progress;
        Outcome    = null;
        Running    = true;

        funnel.LocalInstallStarted();
        _ = RunAsync(embeddings, progress, interrupt.Token, _exited, ct);

        return Task.CompletedTask;
    }

    public Task RetryAsync(CancellationToken ct) {
        if (Running) return Task.CompletedTask;

        funnel.LocalInstallRetried();
        return StartAsync(Embeddings ?? LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Builtin), ct);
    }

    /// Settles a refusal decided before any child would start, through the same path a run's failure takes.
    public void Refuse(string code, string message) {
        if (Running) return;

        var durable = Durable();
        Publish(new LocalInstallOutcome.Failed(code, message, LocalInstallAffordance.Permanent, durable.Pending || durable.CommittedLocal),
            durable, code, quitting: false, _exited);
    }

    /// Forgets a failure the installer refused before changing anything, so the page can check again and start a fresh run.
    public void Forget() {
        if (Running || Outcome is not LocalInstallOutcome.Failed) return;

        Progress = new LocalSetupProgress();
        Outcome  = null;
    }

    /// Asks the child to stop and clean up. Every request after the first reuses it.
    public void Interrupt() {
        if (Running) _interrupt?.Cancel();
    }

    /// Interrupts a running child and completes once its outcome has settled.
    public Task SettleAsync() {
        Interrupt();
        return _exited.Task;
    }

    async Task RunAsync(
            LocalEmbeddingChoice embeddings, LocalSetupProgress progress, CancellationToken interrupt, TaskCompletionSource exited,
            CancellationToken ct) {
        var kcap = cli();
        var unexplained = kcap.CliPath is null ? "cli_missing" : "no_failure_line";
        StreamingResult result;
        try {
            result = await kcap.SetupLocalAsync(embeddings, line => {
                if (line.Kind != ProcessStreamKind.Stdout || LocalSetupLine.Parse(line.Text) is not { } parsed) return;

                post(() => progress.Apply(parsed));
            }, interrupt, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            result = new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, ex.Message)]);
            unexplained = "spawn_failed";
        }

        post(() => Finish(progress, result, unexplained, ct.IsCancellationRequested, exited));
    }

    void Finish(
            LocalSetupProgress progress, StreamingResult result, string unexplained, bool quitting,
            TaskCompletionSource exited) {
        var durable = Durable();
        var outcome = LocalInstallOutcome.Settle(progress, result.ExitCode, durable.Pending || durable.CommittedLocal);
        if (outcome is LocalInstallOutcome.Failed failed) {
            // Without a failed line the stderr tail is the only account of why the child stopped.
            if (progress is { FailedCode: null, FailedMessage: null } && StderrTail.Text(result) is { } stderr)
                outcome = failed with { Message = stderr };
            progress.Halt();
        }

        Publish(outcome, durable, unexplained, quitting, exited);
    }

    void Publish(LocalInstallOutcome outcome, LocalSetupSnapshot durable, string unexplained, bool quitting, TaskCompletionSource exited) {
        Settling?.Invoke(durable);
        _interrupt = null;
        Outcome    = outcome;
        Running    = false;

        switch (outcome) {
            case LocalInstallOutcome.Installed installed:
                funnel.LocalInstallCommitted(installed.Plan);
                break;
            // A run the app's own quit stopped is not an install failure.
            case LocalInstallOutcome.Failed f when !quitting:
                funnel.LocalInstallFailed(f.Code ?? unexplained, f.LockedToLocal);
                break;
        }

        try {
            Settled?.Invoke(outcome);
        } finally {
            exited.TrySetResult();
        }
    }

    LocalSetupSnapshot Durable() => new(LocalSetupState.Pending(root), committedLocal());

    static TaskCompletionSource CompletedSource() {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
