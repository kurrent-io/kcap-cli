using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The last page: what this machine is set up to do, said by its outcome rather than as a checklist.
/// The headline follows what actually happened; while the history import runs, its figures fill in
/// here and the page can be left at any time — the import carries on in the main window.
/// </summary>
public sealed class DoneStepViewModel : ReactiveObject, IWizardStep {
    readonly Func<DoneFacts> _facts;
    readonly IUrlOpener?     _opener;
    readonly Func<bool, Task<WorkspaceReadiness>>? _prepare;

    DoneFacts _current = DoneFacts.Empty;
    WorkspaceReadiness _readiness;
    bool _preparing;
    string? _readyFor;

    /// <param name="prepare">Brings the daemon up and reports the install, answering whether the
    /// dashboard may open; true asks it to try again. Null when there is nothing to wait for.</param>
    public DoneStepViewModel(Func<DoneFacts> facts, IUrlOpener? opener = null, Func<bool, Task<WorkspaceReadiness>>? prepare = null) {
        _facts     = facts;
        _opener    = opener;
        _prepare   = prepare;
        _readiness = prepare is null ? WorkspaceReadiness.Ready : WorkspaceReadiness.Preparing;

        var ready = this.WhenAnyValue(x => x.Readiness).Select(r => r == WorkspaceReadiness.Ready);
        OpenWorkspaceCommand = ReactiveCommand.Create(OpenWorkspace, ready);
        OpenDashboardCommand = ReactiveCommand.Create(OpenDashboard, ready);
        RetryCommand         = ReactiveCommand.CreateFromTask(() => PrepareAsync(retry: true));
    }

    public WizardStepId Id         => WizardStepId.Done;
    public string       Eyebrow    => "Ready";
    public bool         Applicable => true;
    public bool         Satisfied  => true; // a summary step is never itself incomplete
    public string?      NextLabel  => _current.Local ? "Continue" : "Open kcap";

    HistoryImportRun? Import => _current.Import;

    bool Imported => Import is { Imported: > 0 } || Import is { Running: true };

    bool CapturePromised => _current.Recording.Count > 0 && !_current.PathHazard;

    public string Title =>
        Imported          ? "Your work so far"
        : CapturePromised ? "From now on, your agents remember"
        :                   "No new recording connections";

    public string Lede =>
        _current.Local && Import is null && !CapturePromised ? "Setup hasn't added recording connections. You can connect more harnesses whenever you're ready."
        : _current.Local && Import is null ? $"kcap keeps what your coding agents do on {_current.MachineName}, and gives them tools to search it back — so the next time you hit something, what you already worked out is there to find."
        : Import is null && !CapturePromised ? "Setup hasn't added recording connections. Any connections you already had are unchanged. You can connect more harnesses whenever you're ready."
        : Import is null         ? $"kcap keeps what your coding agents do on {_current.MachineName}, and gives them tools to search it back — so the next time you hit something, what you already worked out is there to find."
        : Import.Running         ? "What has landed is already searchable."
        : Import.Imported > 0    ? "Your agents can search all of it — ask one what you have already tried, and it looks here first."
        :                          "No sessions were uploaded. Your history is still on this machine.";

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
        ? $"Your history is still on {_current.MachineName}. Go back to History to retry."
        : $"Your history is still on {_current.MachineName}. Go back to History to retry, or run kcap import later.";

    public bool NothingToCountVisible => Import is null && CapturePromised;

    public string? FooterLine =>
        _current.Local ? "You can connect more harnesses from Help and support → Set up kcap."
        : _current.Recording.Count == 0 ? "Connect a harness from Help and support → Set up kcap."
        : Import is null              ? "kcap import brings your existing history over whenever you want."
        :                               null;

    /// Hooks load at agent start, so an agent open since before setup records nothing until it restarts.
    public string? RestartLine => CapturePromised ? "Agents load new hooks at startup. Restart any agent you already have open." : null;

    public IReadOnlyList<CaptureItem> Capture => [.. _current.Recording.Select(label => new CaptureItem(label, _current.PathHazard))];

    public bool CaptureVisible => _current.Recording.Count > 0;

    public bool PathHazardVisible => _current.PathHazard && _current.Recording.Count > 0;

    public bool CodexNoteVisible => _current.CodexRecording;

    public string DaemonLine => _current.DaemonRunning
        ? $"Running as a service on {_current.MachineName}. It is reachable now, and after a restart."
        : _current.DaemonProblem is { } problem
            ? $"{problem} The app tries again once setup closes."
            : $"The daemon is being set up as a service on {_current.MachineName}. The app keeps it running from here.";

    public WorkspaceReadiness Readiness {
        get => _readiness;
        private set {
            if (_readiness == value) return;
            _readiness = value;
            this.RaisePropertyChanged();
            Restate();
        }
    }

    bool WorkspaceKnown => !_current.Local && !string.IsNullOrEmpty(_current.WorkspaceUrl);

    bool DashboardKnown => _current.Local && !string.IsNullOrEmpty(_current.DashboardUrl);

    bool Opens => WorkspaceKnown || DashboardKnown;

    public bool WorkspaceVisible => WorkspaceKnown && Readiness == WorkspaceReadiness.Ready;

    public bool DashboardVisible => DashboardKnown && Readiness == WorkspaceReadiness.Ready;

    public bool PreparingVisible => Opens && Readiness == WorkspaceReadiness.Preparing;

    public string PreparingLine => _current.Local ? "Getting your dashboard ready…" : "Getting your workspace ready…";

    public bool NotReadyVisible => Opens && Readiness is WorkspaceReadiness.DaemonNotRunning or WorkspaceReadiness.Unreported;

    public string NotReadyTitle => _current.Local ? "The dashboard can't open yet" : "Your workspace can't open yet";

    public string NotReadyBody => Readiness == WorkspaceReadiness.DaemonNotRunning
        ? $"It opens once the daemon is running on {_current.MachineName}."
        : _current.Local
            ? "kcap couldn't reach the local server to register this machine."
            : "kcap couldn't reach your workspace to register this machine. Check your connection.";

    public ReactiveCommand<Unit, Unit> OpenWorkspaceCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenDashboardCommand { get; }

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    /// Re-read on every entry: Back-then-forward can change what the earlier pages did.
    public Task OnEnterAsync(CancellationToken ct) {
        Refresh();
        // Back to sign-in can switch to a server that has not had this install's report.
        if (_prepare is not null && Readiness == WorkspaceReadiness.Ready && _readyFor != Target) Readiness = WorkspaceReadiness.Preparing;
        if (Readiness != WorkspaceReadiness.Ready) _ = PrepareAsync(retry: false);

        return Task.CompletedTask;
    }

    // What the link opens, and so which server must have the report.
    string? Target => _current.Local ? _current.DashboardUrl : _current.WorkspaceUrl;

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

        _preparing = true;
        Readiness  = WorkspaceReadiness.Preparing;
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

        // The daemon line reads the run that just settled.
        Refresh();

        // Sign-in moved to another server while this ran; its answer says nothing about that one.
        if (Target != target) {
            await PrepareAsync(retry: false).ConfigureAwait(true);
            return;
        }

        Readiness = result == WorkspaceReadiness.Preparing ? WorkspaceReadiness.Unreported : result;
        if (Readiness == WorkspaceReadiness.Ready) _readyFor = target;
    }

    // Next finishes the wizard — the shell's own last-step handling raises CloseRequested.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);

    void OnImportChanged(object? sender, PropertyChangedEventArgs e) => Restate();

    void OpenWorkspace() {
        if (Readiness != WorkspaceReadiness.Ready) return;
        if (_current.WorkspaceUrl is not { Length: > 0 } url || _opener is null) return;
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open the workspace: {ex.Message}");
        }
    }

    void OpenDashboard() {
        if (Readiness != WorkspaceReadiness.Ready) return;
        if (_current.DashboardUrl is not { Length: > 0 } url || _opener is null) return;
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not open the dashboard: {ex.Message}");
        }
    }

    void Restate() => this.RaisePropertyChanged(string.Empty);
}
