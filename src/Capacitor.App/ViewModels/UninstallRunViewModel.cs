using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Runs the CLI uninstall, then removes the app itself. Anything that fails before the bundle is
/// trashed leaves the bundle in place, so the app never trashes itself half-cleaned.
public sealed class UninstallRunViewModel : ReactiveObject {
    readonly IKcapCli      _cli;
    readonly IAppRemoval   _removal;
    readonly bool          _purgeLocal;
    readonly Action        _quit;
    readonly Action<Action> _post;
    UninstallRunState      _state = UninstallRunState.Running;

    public UninstallRunViewModel(IKcapCli cli, IAppRemoval removal, bool purgeLocal, Action quit, Action<Action>? post = null) {
        _cli        = cli;
        _removal    = removal;
        _purgeLocal = purgeLocal;
        _quit       = quit;
        _post       = post ?? (action => Dispatcher.UIThread.Post(action));

        var state = this.WhenAnyValue(vm => vm.State);
        RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(vm => vm.CanRetry));
        CloseCommand = ReactiveCommand.Create(quit, state.Select(s => s is UninstallRunState.Failed or UninstallRunState.TrashFailed));
    }

    public UninstallRunState State {
        get => _state;
        private set {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(Heading));
            this.RaisePropertyChanged(nameof(CanRetry));
        }
    }

    public bool CanRetry => State == UninstallRunState.Failed;

    public string Heading => State switch {
        UninstallRunState.Failed      => "kcap was not fully uninstalled",
        UninstallRunState.TrashFailed => $"{_removal.FinishByHand} to finish",
        UninstallRunState.Finished    => "kcap is uninstalled",
        _                             => "Uninstalling kcap…",
    };

    public ObservableCollection<string> Lines { get; } = [];

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// Call on the UI thread; only streamed lines arrive through the injected post.
    public async Task RunAsync() {
        State = UninstallRunState.Running;
        Lines.Clear();

        // Running disables Close, so no exception may leave the run there.
        try {
            await RunStepsAsync();
        } catch (Exception ex) {
            Fail($"The uninstall stopped: {ex.Message}");
        }
    }

    async Task RunStepsAsync() {
        StreamingResult result;
        var streamed = 0;
        try {
            result = await _cli.UninstallAsync(_purgeLocal, line => {
                Interlocked.Increment(ref streamed);
                _post(() => Lines.Add(line.Text));
            }, CancellationToken.None);
        } catch (Exception ex) {
            Fail($"kcap uninstall could not run: {ex.Message}");
            return;
        }

        if (result.ExitCode != 0) {
            // A run that never spawned streams nothing; its reason is only in the tail.
            if (Volatile.Read(ref streamed) == 0)
                foreach (var line in result.Tail) Lines.Add(line.Text);
            Fail($"kcap uninstall exited with code {result.ExitCode}. The app was left in place.");
            return;
        }

        IReadOnlyList<string> left;
        try {
            left = await _removal.RemoveLinksAsync(CancellationToken.None);
        } catch (Exception ex) {
            Fail($"The kcap command links could not be removed: {ex.Message}");
            return;
        }

        if (left.Count > 0) {
            foreach (var path in left) Lines.Add($"Could not remove {path}");
            Fail("The app was left in place. Remove these links, or retry.");
            return;
        }

        try {
            _removal.DeleteSupportFiles();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not remove the app's support files: {ex.Message}");
        }

        bool trashed;
        try {
            trashed = await _removal.TrashBundleAsync(CancellationToken.None);
        } catch (Exception ex) {
            Lines.Add($"The app could not be moved to the Trash: {ex.Message}");
            trashed = false;
        }

        if (!trashed) {
            _removal.RevealBundle();
            Lines.Add($"Everything else is removed. {_removal.FinishByHand} to finish.");
            State = UninstallRunState.TrashFailed;
            return;
        }

        State = UninstallRunState.Finished;
        _quit();
    }

    void Fail(string message) {
        Lines.Add(message);
        State = UninstallRunState.Failed;
    }
}
