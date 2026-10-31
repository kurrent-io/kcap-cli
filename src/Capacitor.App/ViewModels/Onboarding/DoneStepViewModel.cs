using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Setup;
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

    readonly Func<DoneFacts> _facts;
    readonly IUrlOpener?     _opener;
    readonly Func<bool, Task<WorkspaceReadiness>>? _prepare;
    readonly Action?         _addAgent;

    DoneFacts          _current = DoneFacts.Empty;
    WorkspaceReadiness _readiness;
    bool               _preparing;
    string?            _readyFor;
    bool               _entered;
    bool               _detailsVisible;

    /// <param name="prepare">Brings the daemon up and reports the install, answering whether the
    /// dashboard may open; true asks it to try again. Null when there is nothing to wait for.</param>
    /// <param name="restate">Ticks when the daemon or its outcome lines change, so the page re-reads its facts.</param>
    /// <param name="addAgent">Takes the wizard back to the page that connects agents.</param>
    public DoneStepViewModel(
            Func<DoneFacts> facts, IUrlOpener? opener = null, Func<bool, Task<WorkspaceReadiness>>? prepare = null,
            IObservable<Unit>? restate = null, Action? addAgent = null) {
        _facts     = facts;
        _opener    = opener;
        _prepare   = prepare;
        _addAgent  = addAgent;
        _readiness = prepare is null ? WorkspaceReadiness.Ready : WorkspaceReadiness.Preparing;

        var ready = this.WhenAnyValue(x => x.Readiness).Select(r => r == WorkspaceReadiness.Ready);
        OpenInBrowserCommand = ReactiveCommand.Create(OpenInBrowser, ready);
        RetryCommand         = ReactiveCommand.CreateFromTask(() => PrepareAsync(retry: true));
        ToggleDetailsCommand = ReactiveCommand.Create(() => { DetailsVisible = !DetailsVisible; });
        AddAgentCommand      = ReactiveCommand.Create(() => _addAgent?.Invoke());

        restate?.Subscribe(_ => {
            if (_entered) Refresh();
        });
    }

    public WizardStepId Id         => WizardStepId.Done;
    public bool         Applicable => true;
    public bool         Satisfied  => true; // a summary step is never itself incomplete
    public string?      NextLabel  => "Open kcap";

    public BackgroundServiceState ServiceState =>
        _current.DaemonRunning ? BackgroundServiceState.Running
        : !_current.DaemonBusy && (_current.DaemonProblem is not null || Readiness == WorkspaceReadiness.DaemonNotRunning)
            ? BackgroundServiceState.NotRunning
            : BackgroundServiceState.Installing;

    public string Eyebrow => ServiceState switch {
        BackgroundServiceState.Running    => "Ready",
        BackgroundServiceState.NotRunning => "One thing left",
        _                                 => "Finishing up",
    };

    /// Setup can only be left unfinished while something failed; otherwise Open kcap closes it.
    public bool OffersFinishLater => ServiceFailed || NotReadyVisible;

    HistoryImportRun? Import => _current.Import;

    bool Imported => Import is { Imported: > 0 } || Import is { Running: true };

    bool CapturePromised => _current.Recording.Count > 0 && !_current.PathHazard;

    public string Title =>
        Imported          ? "Your work so far"
        : CapturePromised ? "From now on, your agents remember"
        :                   "No new coding tools connected";

    public string Lede =>
        _current.Local && Import is null && !CapturePromised ? $"Setup didn't connect any coding tools. {ConnectLater}"
        : Import is null && !CapturePromised ? $"Setup didn't connect any new coding tools, and any you connected before are unchanged. {ConnectLater}"
        : Import is null         ? $"kcap stores what your coding agents do on {_current.MachineName} and lets them search it later. Next time you hit a problem, the fix you already found is stored in kcap"
        : Import.Running         ? "What has landed is already searchable"
        : Import.Imported > 0    ? "Your agents can search all of it. Ask one what you have already tried, and it looks here first"
        :                          "No sessions were imported. Your history is still on this machine";

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

    public double ProgressValue => Import?.Progress ?? 0;

    public string ActivityLine => Import switch {
        { Processed: 0 } => "Looking through your history…",
        { } run          => $"{run.ProcessedOfExpected} {run.SessionsNoun} checked",
        null             => "",
    };

    public bool FailedVisible => Import is { State: ImportRunState.Failed or ImportRunState.Cancelled } or { Running: false, Failed: > 0 };

    public string FailedTitle => Import is { Failed: 0 } ? "History import did not finish"
        : Import is { Failed: 1 } ? "1 session failed to import" : $"{Import?.Failed ?? 0} sessions failed to import";

    public string FailedBody => _current.Local
        ? $"Your history is still on {_current.MachineName}. Go back to History to retry"
        : $"Your history is still on {_current.MachineName}. Go back to History to retry, or run kcap import later";

    /// Hooks load at agent start, so an agent open since before setup stores nothing until it is launched again,
    /// and when setup changed PATH, a terminal open since before it cannot run the hooks either.
    public string? RestartLine => !CapturePromised ? null
        : _current.PathAdded ? "Agents already open will start storing sessions once you launch them again from a new terminal"
        : "Agents already open will start storing sessions the next time you launch them";

    public IReadOnlyList<CaptureItem> Capture => [.. _current.Recording.Select(label => new CaptureItem(label, _current.PathHazard))];

    public bool CaptureVisible => _current.Recording.Count > 0;

    public bool NoAgentsVisible => _current.Recording.Count == 0;

    /// The lede already names Settings → Harnesses when nothing was imported either.
    public string NoAgentsLine => Import is null
        ? "No coding tools were connected in this setup"
        : $"No coding tools were connected in this setup. {ConnectLater}";

    const string ConnectLater = $"You can connect them later in {SetupLocation.HarnessesSettings}";

    public bool FirstSessionVisible => Import is null && CapturePromised;

    public bool PathHazardVisible => _current.PathHazard && _current.Recording.Count > 0;

    public bool CodexNoteVisible => _current.CodexRecording;

    public bool AddAgentVisible => _addAgent is not null;

    public string AddAgentLabel => _current.Recording.Count > 0 ? "Add another agent" : "Add an agent";

    /// The CLI's own import is named only where the machine has a terminal command to offer.
    public IReadOnlyList<DisclosureSpan>? ImportHint =>
        !_current.Local && _current.Recording.Count > 0 && Import is null
            ? [new("kcap import", Code: true), new(" brings your existing history over whenever you want")]
            : null;

    /// Starting agents remotely goes through a cloud workspace, so the local edition does not offer it.
    public string ServiceBody => _current.Local
        ? "Keeps kcap storing sessions when the app is closed"
        : $"Keeps kcap storing sessions when the app is closed, and lets you start agents on {ThisDevice.Name} from anywhere";

    public string ServiceStatus => ServiceState switch {
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

    public bool ServiceDetailsAvailable => ServiceDetails.Length > 0;

    /// An outcome line the daemon reported while the service is not failed, which See details does not show.
    public string? ServiceNote => ServiceFailed ? null : _current.ServiceDetail;

    public bool RetryAvailable => _prepare is not null;

    public bool DetailsVisible {
        get => _detailsVisible && ServiceFailed;
        private set {
            _detailsVisible = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(DetailsLabel));
        }
    }

    public string DetailsLabel => DetailsVisible ? "Hide details" : "See details";

    public WorkspaceReadiness Readiness {
        get => _readiness;
        private set {
            if (_readiness == value) return;
            _readiness = value;
            this.RaisePropertyChanged();
            Restate();
        }
    }

    // What Open in browser opens, and so which server must have the install's report.
    string? Target => _current.Local ? _current.DashboardUrl : _current.WorkspaceUrl;

    bool Opens => !string.IsNullOrEmpty(Target);

    /// The web workspace, or the local server's dashboard, once this machine is registered with it.
    public bool OpenInBrowserVisible => Opens && Readiness == WorkspaceReadiness.Ready;

    public bool PreparingVisible => Opens && Readiness == WorkspaceReadiness.Preparing;

    public string PreparingLine => _current.Local ? "Getting your dashboard ready…" : "Getting your workspace ready…";

    /// A daemon that is not running is the service card's to say; this is the server not taking the report.
    public bool NotReadyVisible => Opens && Readiness == WorkspaceReadiness.Unreported;

    public string NotReadyTitle => _current.Local ? "The dashboard can't open yet" : "Your workspace can't open yet";

    public string NotReadyBody => _current.Local
        ? "kcap couldn't reach the local server to register this machine"
        : "kcap couldn't reach your workspace to register this machine. Check your connection";

    public ReactiveCommand<Unit, Unit> OpenInBrowserCommand { get; }

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleDetailsCommand { get; }

    public ReactiveCommand<Unit, Unit> AddAgentCommand { get; }

    /// Re-read on every entry: Back-then-forward can change what the earlier pages did.
    public Task OnEnterAsync(CancellationToken ct) {
        _entered = true;
        Refresh();
        // Back to sign-in can switch to a server that has not had this install's report.
        if (_prepare is not null && Readiness == WorkspaceReadiness.Ready && _readyFor != Target) Readiness = WorkspaceReadiness.Preparing;
        if (Readiness != WorkspaceReadiness.Ready) _ = PrepareAsync(retry: false);

        return Task.CompletedTask;
    }

    // Next finishes the wizard — the shell's own last-step handling raises CloseRequested.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        _entered = false;
        return Task.FromResult(true);
    }

    void Refresh() {
        if (_current.Import is INotifyPropertyChanged before) before.PropertyChanged -= OnImportChanged;
        _current = _facts();
        if (_current.Import is INotifyPropertyChanged after) after.PropertyChanged += OnImportChanged;
        Restate();
    }

    /// Ready holds for the server that took the report: a later failure cannot make it ask again,
    /// but a different server has to be prepared afresh.
    async Task PrepareAsync(bool retry) {
        if (_prepare is null || _preparing || Readiness == WorkspaceReadiness.Ready) return;

        _preparing      = true;
        _detailsVisible = false;
        Readiness       = WorkspaceReadiness.Preparing;
        var target = Target;
        WorkspaceReadiness result;
        try {
            result = await _prepare(retry).ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not get the workspace ready: {ex.Message}");
            result = WorkspaceReadiness.Unreported;
        } finally {
            _preparing = false;
        }

        // The service card reads the run that just settled.
        Refresh();

        // Sign-in moved to another server while this ran; its answer says nothing about that one.
        if (Target != target) {
            await PrepareAsync(retry: false).ConfigureAwait(true);
            return;
        }

        Readiness = result == WorkspaceReadiness.Preparing ? WorkspaceReadiness.Unreported : result;
        if (Readiness == WorkspaceReadiness.Ready) _readyFor = target;
    }

    void OnImportChanged(object? sender, PropertyChangedEventArgs e) => Restate();

    void OpenInBrowser() {
        if (Readiness != WorkspaceReadiness.Ready) return;
        if (Target is not { Length: > 0 } url || _opener is null) return;
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open {url}: {ex.Message}");
        }
    }

    void Restate() => this.RaisePropertyChanged(string.Empty);
}
