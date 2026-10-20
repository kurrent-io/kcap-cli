using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// Owns the one `kcap setup --local` child that changes a committed installation's search by meaning, for the
/// app's lifetime. The child reuses the installation, so a changed model restarts the server. Lines and the
/// settled outcome reach the UI through <c>post</c>, in order.
/// </summary>
/// <param name="held">True while another app-owned change to the installation runs; a start then does nothing.</param>
public sealed class LocalSearchController(Func<IKcapCli> cli, Action<Action> post, Func<bool>? held = null) : ReactiveObject {
    LocalSetupProgress       _progress = new();
    bool                     _running;
    LocalInstallOutcome?     _outcome;
    CancellationTokenSource? _interrupt;
    TaskCompletionSource     _exited = CompletedSource();

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

    /// Raised once per run, through <c>post</c>, after <see cref="Outcome"/> is set.
    public event Action<LocalInstallOutcome>? Settled;

    /// Spawns the child and returns; a no-op while a run is in flight or once <paramref name="ct"/>, the app's
    /// shutdown, is cancelled.
    public Task StartAsync(LocalEmbeddingChoice embeddings, CancellationToken ct) {
        if (Running || ct.IsCancellationRequested || held?.Invoke() == true) return Task.CompletedTask;

        var progress  = new LocalSetupProgress();
        var interrupt = new CancellationTokenSource();
        _interrupt = interrupt;
        _exited    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress   = progress;
        Outcome    = null;
        Running    = true;

        _ = RunAsync(embeddings, progress, interrupt.Token, _exited, ct);

        return Task.CompletedTask;
    }

    /// Interrupts a running child and completes once its outcome has settled.
    public Task SettleAsync() {
        if (Running) _interrupt?.Cancel();
        return _exited.Task;
    }

    async Task RunAsync(
            LocalEmbeddingChoice embeddings, LocalSetupProgress progress, CancellationToken interrupt, TaskCompletionSource exited,
            CancellationToken ct) {
        StreamingResult result;
        try {
            result = await cli().SetupLocalAsync(embeddings, line => {
                if (line.Kind != ProcessStreamKind.Stdout || LocalSetupLine.Parse(line.Text) is not { } parsed) return;

                post(() => progress.Apply(parsed));
            }, interrupt, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            result = new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, ex.Message)]);
        }

        post(() => Finish(progress, result, exited));
    }

    void Finish(LocalSetupProgress progress, StreamingResult result, TaskCompletionSource exited) {
        var outcome = LocalInstallOutcome.Settle(progress, result.ExitCode, lockedToLocal: true);
        if (outcome is LocalInstallOutcome.Failed failed) {
            if (progress is { FailedCode: null, FailedMessage: null } && StderrTail.Text(result) is { } stderr)
                outcome = failed with { Message = stderr };
            progress.Halt();
        }

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
