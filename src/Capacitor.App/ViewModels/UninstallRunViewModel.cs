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
    readonly Action<Action> _post;
    readonly Func<Task>?    _prepare;
    bool                   _prepared;
    bool                   _detailsExpanded;
    UninstallRunState      _state = UninstallRunState.Running;

    public UninstallRunViewModel(IKcapCli cli, IAppRemoval removal, bool purgeLocal, Action quit, Action<Action>? post = null,
            Func<Task>? prepare = null) {
        _cli        = cli;
        _removal    = removal;
        _purgeLocal = purgeLocal;
        _post       = post ?? (action => Dispatcher.UIThread.Post(action));
        _prepare    = prepare;
        _prepared   = prepare is null;
        _state      = _prepared ? UninstallRunState.Running : UninstallRunState.Preparing;

        var state = this.WhenAnyValue(vm => vm.State);
        RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(vm => vm.CanRetry));
        CloseCommand = ReactiveCommand.Create(quit, state.Select(s => s is UninstallRunState.Failed or UninstallRunState.TrashFailed or UninstallRunState.Finished));
        ToggleDetailsCommand = ReactiveCommand.Create(() => DetailsExpanded = !DetailsExpanded);
    }

    public UninstallRunState State {
        get => _state;
        private set {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(Heading));
            this.RaisePropertyChanged(nameof(CanRetry));
            this.RaisePropertyChanged(nameof(IsFinished));
            this.RaisePropertyChanged(nameof(IsBusy));
            this.RaisePropertyChanged(nameof(Description));
            this.RaisePropertyChanged(nameof(ProgressValue));
            this.RaisePropertyChanged(nameof(CloseButtonText));
            this.RaisePropertyChanged(nameof(DetailsLabel));
            if (value is UninstallRunState.Failed or UninstallRunState.TrashFailed) DetailsExpanded = true;
        }
    }

    public bool CanRetry => _prepared && State == UninstallRunState.Failed;
    public bool IsFinished => State == UninstallRunState.Finished;
    public bool IsBusy => State is UninstallRunState.Preparing or UninstallRunState.Running;
    public double ProgressValue => IsFinished ? 100 : 0;
    public string CloseButtonText => IsFinished ? "Done" : "Close";

    public bool DetailsExpanded {
        get => _detailsExpanded;
        private set {
            this.RaiseAndSetIfChanged(ref _detailsExpanded, value);
            this.RaisePropertyChanged(nameof(DetailsLabel));
        }
    }

    public string DetailsLabel => (DetailsExpanded, IsFinished) switch {
        (false, true)  => "Show what was removed",
        (true, true)   => "Hide what was removed",
        (false, false) => "Show details",
        (true, false)  => "Hide details",
    };

    public string Description => State switch {
        UninstallRunState.Preparing => "Closing kcap before removing its background services and app.",
        UninstallRunState.Finished => "kcap’s background services and coding-agent integrations have been removed. The app is in the Trash.",
        UninstallRunState.Failed => "Uninstall could not finish. See the details below.",
        UninstallRunState.TrashFailed => "The app could not be moved to the Trash. See the details below to finish.",
        _ => "Removing background services, coding-agent integrations, and the app.",
    };

    public string Heading => State switch {
        UninstallRunState.Failed      => "kcap was not fully uninstalled",
        UninstallRunState.TrashFailed => $"{_removal.FinishByHand} to finish",
        UninstallRunState.Finished    => "kcap is uninstalled",
        _                             => "Uninstalling kcap…",
    };

    public ObservableCollection<string> Lines { get; } = [];

    /// What a finished run removed, in the user's terms; the raw <see cref="Lines"/> are for a run that
    /// is still going or has failed.
    public ObservableCollection<UninstallReceiptItem> Receipt { get; } = [];

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleDetailsCommand { get; }

    /// Call on the UI thread; only streamed lines arrive through the injected post.
    public async Task RunAsync() {
        Lines.Clear();
        Receipt.Clear();

        // Running disables Close, so no exception may leave the run there.
        try {
            if (!_prepared) {
                State = UninstallRunState.Preparing;
                Lines.Add("Closing kcap…");
                await _prepare!();
                _prepared = true;
            }
            State = UninstallRunState.Running;
            await RunStepsAsync();
        } catch (Exception ex) {
            Fail($"The uninstall stopped: {ex.Message}");
        }
    }

    async Task RunStepsAsync() {
        StreamingResult result;
        var streamed = 0;
        var purged = 0;
        try {
            result = await _cli.UninstallAsync(_purgeLocal, line => {
                Interlocked.Increment(ref streamed);
                // The CLI's own outcome line: a purge that was asked for may have found no server to purge.
                if (line.Text.StartsWith("Purged the local server", StringComparison.Ordinal)) Interlocked.Exchange(ref purged, 1);
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
        Lines.Add("Removed kcap command links.");

        var supportFilesLeft = false;
        try {
            Lines.Add("Cleaning up app support files…");
            supportFilesLeft = !_removal.DeleteSupportFiles();
        } catch (Exception ex) {
            supportFilesLeft = true;
            Lines.Add($"Some app support files could not be removed: {ex.Message}");
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

        Lines.Add("Moved the app to the Trash.");
        FillReceipt(Volatile.Read(ref purged) == 1, supportFilesLeft);
        State = UninstallRunState.Finished;
    }

    /// Claims only what a zero exit from `kcap uninstall` and the steps after it guarantee.
    void FillReceipt(bool purgedLocal, bool supportFilesLeft) {
        Receipt.Add(new("Removed kcap from your coding agents’ user settings"));
        Receipt.Add(new("Removed kcap’s background services"));
        if (purgedLocal) Receipt.Add(new("Deleted the local server and every session it recorded"));
        Receipt.Add(new("Removed your kcap settings and sign-in"));
        Receipt.Add(new("Removed the kcap command the app installed"));
        Receipt.Add(new(_removal.Trashed));
        if (supportFilesLeft)
            Receipt.Add(new("A few of the app’s own files couldn’t be removed. They don’t keep kcap running.", Done: false));
    }

    void Fail(string message) {
        Lines.Add(message);
        State = UninstallRunState.Failed;
    }
}
