using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// Owns the one `kcap local upgrade` child for the app's lifetime. Lines and the settled outcome
/// reach the UI through <c>post</c>, in order, so the outcome is always decided after every line the
/// child printed has been applied.
/// </summary>
/// <param name="held">True while another app-owned change to the installation runs; a start then does nothing,
/// since its child would only find the installation busy.</param>
public sealed class LocalUpgradeController(Func<IKcapCli> cli, Action<Action> post, Func<bool>? held = null) : ReactiveObject {
    LocalUpgradeProgress     _progress = new();
    bool                     _running;
    LocalUpgradeOutcome?     _outcome;
    CancellationTokenSource? _interrupt;
    TaskCompletionSource     _exited = CompletedSource();

    public LocalUpgradeProgress Progress {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool Running {
        get => _running;
        private set => this.RaiseAndSetIfChanged(ref _running, value);
    }

    public LocalUpgradeOutcome? Outcome {
        get => _outcome;
        private set => this.RaiseAndSetIfChanged(ref _outcome, value);
    }

    /// Raised once per run, through <c>post</c>, after <see cref="Outcome"/> is set.
    public event Action<LocalUpgradeOutcome>? Settled;

    /// Runs the upgrade only when this app version still owes its post-update work, on a machine whose server
    /// is its committed local installation and with no local setup pending. True when it started.
    public async Task<bool> StartAfterUpdateAsync(bool owed, bool committedLocal, Func<bool> setupPending, CancellationToken ct) {
        if (!owed || !committedLocal) return false;
        // Resumes on the caller's context: StartAsync changes bound properties.
        try {
            if (await Task.Run(setupPending, ct) || ct.IsCancellationRequested) return false;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false;
        }

        if (Running || held?.Invoke() == true || ct.IsCancellationRequested) return false;
        await StartAsync(ct);
        return true;
    }

    /// Spawns the child and returns; a no-op while a run is in flight or once <paramref name="ct"/>, the
    /// app's shutdown, is cancelled: a quit's settle may already have found nothing to wait for.
    public Task StartAsync(CancellationToken ct) {
        if (Running || ct.IsCancellationRequested || held?.Invoke() == true) return Task.CompletedTask;

        var progress  = new LocalUpgradeProgress();
        var interrupt = new CancellationTokenSource();
        _interrupt = interrupt;
        _exited    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress   = progress;
        Outcome    = null;
        Running    = true;

        _ = RunAsync(progress, interrupt.Token, _exited, ct);

        return Task.CompletedTask;
    }

    /// Asks the child to stop. Only its download stops for it; every request after the first reuses it.
    public void Interrupt() {
        if (Running) _interrupt?.Cancel();
    }

    /// Interrupts a running child and completes once its outcome has settled.
    public Task SettleAsync() {
        Interrupt();
        return _exited.Task;
    }

    async Task RunAsync(LocalUpgradeProgress progress, CancellationToken interrupt, TaskCompletionSource exited, CancellationToken ct) {
        StreamingResult result;
        try {
            result = await cli().LocalUpgradeAsync(line => {
                if (line.Kind != ProcessStreamKind.Stdout || LocalSetupLine.Parse(line.Text) is not { } parsed) return;

                post(() => progress.Apply(parsed));
            }, interrupt, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            result = new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, ex.Message)]);
        }

        post(() => Finish(progress, result, exited));
    }

    void Finish(LocalUpgradeProgress progress, StreamingResult result, TaskCompletionSource exited) {
        var outcome = LocalUpgradeOutcome.Settle(progress, result.ExitCode, StderrTail.Text(result));
        _interrupt = null;
        Outcome    = outcome;
        Running    = false;

        try {
            Settled?.Invoke(outcome);
        } finally {
            exited.TrySetResult();
        }
    }

    static TaskCompletionSource CompletedSource() {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
