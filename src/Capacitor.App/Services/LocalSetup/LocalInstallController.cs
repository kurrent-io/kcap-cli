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

    /// Raised once per run, through <c>post</c>, after <see cref="Outcome"/> is set.
    public event Action<LocalInstallOutcome>? Settled;

    /// Spawns the child and returns; a no-op while a run is in flight.
    public Task StartAsync(CancellationToken ct) {
        if (Running) return Task.CompletedTask;

        var progress = new LocalSetupProgress();
        var interrupt = new CancellationTokenSource();
        _interrupt = interrupt;
        _exited    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress   = progress;
        Outcome    = null;
        Running    = true;

        funnel.LocalInstallStarted();
        _ = RunAsync(progress, interrupt.Token, _exited, ct);

        return Task.CompletedTask;
    }

    public Task RetryAsync(CancellationToken ct) {
        if (Running) return Task.CompletedTask;

        funnel.LocalInstallRetried();
        return StartAsync(ct);
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

    async Task RunAsync(LocalSetupProgress progress, CancellationToken interrupt, TaskCompletionSource exited, CancellationToken ct) {
        var sawLine = false;
        StreamingResult result;
        try {
            result = await cli().SetupLocalAsync(line => {
                if (line.Kind != ProcessStreamKind.Stdout || LocalSetupLine.Parse(line.Text) is not { } parsed) return;

                sawLine = true;
                post(() => progress.Apply(parsed));
            }, interrupt, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            result = new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, ex.Message)]);
        }

        var ranWithoutOutput = result.ExitCode == -1 && !sawLine;
        post(() => Finish(progress, result, ranWithoutOutput, exited));
    }

    void Finish(LocalSetupProgress progress, StreamingResult result, bool ranWithoutOutput, TaskCompletionSource exited) {
        var outcome = LocalInstallOutcome.Settle(progress, result.ExitCode, LockedToLocal());
        if (outcome is LocalInstallOutcome.Failed failed && ranWithoutOutput && StderrText(result) is { } stderr)
            outcome = failed with { Message = stderr };

        _interrupt = null;
        Outcome    = outcome;
        Running    = false;

        switch (outcome) {
            case LocalInstallOutcome.Installed installed:
                funnel.LocalInstallCommitted(installed.Plan);
                break;
            case LocalInstallOutcome.Failed f:
                funnel.LocalInstallFailed(f.Code ?? "none", f.LockedToLocal);
                break;
        }

        try {
            Settled?.Invoke(outcome);
        } finally {
            exited.TrySetResult();
        }
    }

    bool LockedToLocal() => LocalSetupState.Pending(root) || committedLocal();

    static string? StderrText(StreamingResult result) {
        var lines = result.Tail.Where(l => l.Kind == ProcessStreamKind.Stderr && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => l.Text.Trim()).ToList();

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    static TaskCompletionSource CompletedSource() {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
