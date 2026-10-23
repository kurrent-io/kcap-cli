using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The last page: what this machine is set up to do, said by its outcome rather than as a checklist.
/// The headline follows what actually happened and the eyebrow follows the background service; while
/// the history import runs, its figures fill in here and the page can be left at any time.
/// </summary>
public sealed class DoneStepViewModel : ReactiveObject, IWizardStep {
    public const string ServiceTitle = "Background service";
    public const string FirstSessionLine = "Your next session will be the first one stored";
    public const string RestartNote = "Agents already open will start storing sessions the next time you launch them";

    static readonly string ThisDevice = OperatingSystem.IsMacOS() ? "this Mac" : "this computer";

    readonly Func<DoneFacts> _facts;
    readonly IUrlOpener?     _opener;
    readonly Func<Task>?     _retryService;
    readonly Action?         _addAgent;

    DoneFacts _current = DoneFacts.Empty;
    bool      _entered;
    bool      _detailsVisible;

    /// <param name="restate">Ticks when the daemon or its outcome lines change, so the page re-reads its facts.</param>
    /// <param name="retryService">Runs the daemon enable again.</param>
    /// <param name="addAgent">Takes the wizard back to the page that connects agents.</param>
    public DoneStepViewModel(
            Func<DoneFacts> facts, IUrlOpener? opener = null, IObservable<Unit>? restate = null,
            Func<Task>? retryService = null, Action? addAgent = null) {
        _facts        = facts;
        _opener       = opener;
        _retryService = retryService;
        _addAgent     = addAgent;

        OpenInBrowserCommand  = ReactiveCommand.Create(OpenInBrowser);
        RetryServiceCommand   = ReactiveCommand.CreateFromTask(RetryServiceAsync);
        ToggleDetailsCommand  = ReactiveCommand.Create(() => { DetailsVisible = !DetailsVisible; });
        AddAgentCommand       = ReactiveCommand.Create(() => _addAgent?.Invoke());

        restate?.Subscribe(_ => {
            if (_entered) Refresh();
        });
    }

    public WizardStepId Id         => WizardStepId.Done;
    public bool         Applicable => true;
    public bool         Satisfied  => true; // a summary step is never itself incomplete
    public string?      NextLabel  => "Open kcap";

    public BackgroundServiceState ServiceState =>
        _current.DaemonRunning                                   ? BackgroundServiceState.Running
        : !_current.DaemonBusy && _current.DaemonProblem is not null ? BackgroundServiceState.NotRunning
        :                                                          BackgroundServiceState.Installing;

    public string Eyebrow => ServiceState switch {
        BackgroundServiceState.Running    => "Ready",
        BackgroundServiceState.NotRunning => "One thing left",
        _                                 => "Finishing up",
    };

    /// Setup can only be left unfinished while the service is not running; otherwise Open kcap closes it.
    public bool OffersFinishLater => ServiceState == BackgroundServiceState.NotRunning;

    HistoryImportRun? Import => _current.Import;

    bool Imported => Import is { Imported: > 0 } || Import is { Running: true };

    bool CapturePromised => _current.Recording.Count > 0 && !_current.PathHazard;

    public string Title =>
        Imported          ? "Your work so far"
        : CapturePromised ? "From now on, your agents remember"
        :                   "No new agents connected";

    public string Lede =>
        _current.Local && Import is null && !CapturePromised ? "Setup didn't connect any agents. You can add one whenever you're ready"
        : Import is null && !CapturePromised ? "Setup didn't connect any new agents, and any you connected before are unchanged. You can add one whenever you're ready"
        : Import is null         ? $"kcap stores what your coding agents do on {_current.MachineName} and lets them search it later. Next time you hit a problem, the fix you already found is stored in kcap"
        : Import.Running         ? "What has landed is already searchable"
        : Import.Imported > 0    ? "Your agents can search all of it. Ask one what you have already tried, and it looks here first"
        :                          "No sessions were uploaded. Your history is still on this machine";

    public bool ShowsFigures => Import is not null;

    public bool Running => Import is { Running: true };

    public string LandedValue => Import?.Imported.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    /// "/ 324" when the selection's count is an honest denominator, "sessions" otherwise.
    public string LandedUnit => Import is { Expected: > 0 } run && run.Imported <= run.Expected
        ? $"/ {run.Expected.ToString("N0", CultureInfo.CurrentCulture)}"
        : "sessions";

    public string LandedCaption => Import?.WindowCaption ?? "";

    public string RepositoriesValue => Import?.Repositories.ToString(CultureInfo.CurrentCulture) ?? "—";

    public string RepositoriesCaption => "selected from this machine";

    public string RepositoriesUnit => Import is { Repositories: 1 } ? "repository" : "repositories";

    /// Sessions settled against the selection's count; passes when there is no count to measure against.
    /// Too-short and excluded sessions never settle, so the bar can stop short of full before the run ends.
    public double ProgressValue => Import switch {
        { Expected: > 0 } run => Math.Min(100.0, 100.0 * run.Processed / run.Expected),
        { Passes: > 0 } run   => 100.0 * run.PassesDone / run.Passes,
        _                     => 0,
    };

    public string ActivityLine => Import switch {
        { Processed: 0 }                  => "Looking through your history…",
        { Expected: > 0 } run when run.Processed <= run.Expected
            => $"{run.Processed.ToString("N0", CultureInfo.CurrentCulture)} of {run.Expected.ToString("N0", CultureInfo.CurrentCulture)} sessions checked",
        { } run                           => $"{run.Processed.ToString("N0", CultureInfo.CurrentCulture)} sessions checked",
        null                              => "",
    };

    public bool FailedVisible => Import is { State: ImportRunState.Failed or ImportRunState.Cancelled } or { Running: false, Failed: > 0 };

    public string FailedTitle => Import is { Failed: 0 } ? "History import did not finish"
        : Import is { Failed: 1 } ? "1 session failed to upload" : $"{Import?.Failed ?? 0} sessions failed to upload";

    public string FailedBody => _current.Local
        ? $"Your history is still on {_current.MachineName}. Go back to History to retry"
        : $"Your history is still on {_current.MachineName}. Go back to History to retry, or run kcap import later";

    public IReadOnlyList<CaptureItem> Capture => [.. _current.Recording.Select(label => new CaptureItem(label, _current.PathHazard))];

    public bool CaptureVisible => _current.Recording.Count > 0;

    public bool NoAgentsVisible => _current.Recording.Count == 0;

    public bool FirstSessionVisible => Import is null && CapturePromised;

    public bool PathHazardVisible => _current.PathHazard && _current.Recording.Count > 0;

    public bool CodexNoteVisible => _current.CodexRecording;

    public bool AddAgentVisible => _addAgent is not null;

    public string AddAgentLabel => _current.Recording.Count > 0 ? "Add another agent" : "Add an agent";

    /// The CLI's own import is named only where the machine has a terminal command to offer.
    public string? ImportHint =>
        !_current.Local && _current.Recording.Count > 0 && Import is null
            ? "kcap import brings your existing history over whenever you want"
            : null;

    public string ServiceBody =>
        $"Keeps kcap storing sessions when the app is closed, and lets you start agents on {ThisDevice} from anywhere";

    public string ServicePill => ServiceState switch {
        BackgroundServiceState.Running    => "Running",
        BackgroundServiceState.NotRunning => "Not running",
        _                                 => "Installing…",
    };

    public bool ServiceRunning => ServiceState == BackgroundServiceState.Running;

    public bool ServiceInstalling => ServiceState == BackgroundServiceState.Installing;

    public bool ServiceFailed => ServiceState == BackgroundServiceState.NotRunning;

    public string ServiceProblemTitle => $"kcap couldn't start the background service on {_current.MachineName}";

    /// The daemon's own account of what stopped it, behind See details.
    public string ServiceDetails {
        get {
            var lines = new List<string>(2);
            if (_current.DaemonProblem is { Length: > 0 } problem) lines.Add(problem);
            if (_current.ServiceDetail is { Length: > 0 } detail && !lines.Contains(detail)) lines.Add(detail);

            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool RetryAvailable => _retryService is not null;

    public bool DetailsVisible {
        get => _detailsVisible && ServiceFailed;
        private set {
            _detailsVisible = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(DetailsLabel));
        }
    }

    public string DetailsLabel => DetailsVisible ? "Hide details" : "See details";

    /// The web workspace, or the local server's dashboard.
    string? BrowserUrl => _current.Local ? _current.DashboardUrl : _current.WorkspaceUrl;

    public bool OpenInBrowserVisible => !string.IsNullOrEmpty(BrowserUrl);

    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }

    public ReactiveCommand<Unit, Unit> RetryServiceCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleDetailsCommand { get; }

    public ReactiveCommand<Unit, Unit> AddAgentCommand { get; }

    /// Re-read on every entry: Back-then-forward can change what the earlier pages did.
    public Task OnEnterAsync(CancellationToken ct) {
        _entered = true;
        Refresh();

        return Task.CompletedTask;
    }

    // Next finishes the wizard — the shell's own last-step handling raises CloseRequested.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);

    void Refresh() {
        if (_current.Import is INotifyPropertyChanged before) before.PropertyChanged -= OnImportChanged;
        _current = _facts();
        if (_current.Import is INotifyPropertyChanged after) after.PropertyChanged += OnImportChanged;
        Restate();
    }

    void OnImportChanged(object? sender, PropertyChangedEventArgs e) => Restate();

    async Task RetryServiceAsync() {
        if (_retryService is null || !ServiceFailed) return;

        _detailsVisible = false;
        try {
            await _retryService().ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: retrying the background service failed unexpectedly: {ex.Message}");
        }

        Refresh();
    }

    void OpenInBrowser() {
        if (BrowserUrl is not { Length: > 0 } url || _opener is null) return;
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open {url}: {ex.Message}");
        }
    }

    void Restate() => this.RaisePropertyChanged(string.Empty);
}
