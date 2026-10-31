using Capacitor.Cli.Core;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// One `kcap local fetch --json` per app lifetime, started while the edition page offers the local edition so the
/// install finds the bundle already in the CLI's cache. A cancelled or failed fetch is not retried: the install
/// downloads whatever the cache lacks.
/// </summary>
public sealed class LocalBundlePrefetch(Func<IKcapCli> cli, CancellationToken lifetime) {
    readonly Lock _lock = new();
    CancellationTokenSource? _interrupt;
    Task _completion = Task.CompletedTask;
    bool _started;

    /// Each stdout line the fetch prints, raised on the thread that read it.
    public event Action<LocalSetupLine>? Line;

    public bool Started {
        get {
            lock (_lock) return _started;
        }
    }

    public bool Running => !_completion.IsCompleted;

    /// Completes once the fetch has exited, however it ended; never faults.
    public Task Completion => _completion;

    /// Spawns the fetch the first time it is called; every later call does nothing.
    public void Start() {
        lock (_lock) {
            if (_started) return;
            _started = true;
            var interrupt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _interrupt  = interrupt;
            _completion = Task.Run(() => RunAsync(interrupt.Token), CancellationToken.None);
        }
    }

    /// Asks a running fetch to stop.
    public void Cancel() {
        lock (_lock) {
            if (_interrupt is { IsCancellationRequested: false } interrupt) interrupt.Cancel();
        }
    }

    async Task RunAsync(CancellationToken interrupt) {
        try {
            var result = await cli().LocalFetchAsync(line => {
                if (line.Kind == ProcessStreamKind.Stdout && LocalSetupLine.Parse(line.Text) is { } parsed) Line?.Invoke(parsed);
            }, interrupt).ConfigureAwait(false);
            if (result.ExitCode != 0 && !interrupt.IsCancellationRequested)
                Console.Error.WriteLine($"kcap app: fetching the local bundle ahead of the install stopped (exit {result.ExitCode}).");
        } catch (OperationCanceledException) when (interrupt.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: fetching the local bundle ahead of the install failed: {ex.Message}");
        }
    }
}
