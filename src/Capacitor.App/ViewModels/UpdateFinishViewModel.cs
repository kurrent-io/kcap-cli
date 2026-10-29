using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// The window a relaunched app shows while it finishes an update: the app is already the new version, so what
/// remains is the local server's upgrade, when there is one, and the daemon coming back as the new version.
/// Each step is projected from the same sources the main window reads. Once every step is done the window hands
/// over to the main window by itself; a note or a problem keeps it open with the outcome until the user goes on.
/// </summary>
public sealed class UpdateFinishViewModel : ReactiveObject, IDisposable {
    internal static readonly TimeSpan HandOverDelay = TimeSpan.FromSeconds(1.5);
    /// The daemon notices its new binary within its 15 s poll and restarts in seconds, and redials the server within
    /// its 30 s backoff; past this, a daemon that has done neither is not going to while the window waits. Counted
    /// again once a local server upgrade settles, since the daemon cannot reach that server before then.
    internal static readonly TimeSpan DaemonPatience = TimeSpan.FromSeconds(60);
    internal const string ServerConnected = "connected";

    readonly CompositeDisposable _disposables = new();
    readonly BehaviorSubject<bool> _waitedLong = new(false);
    readonly SerialDisposable _patience = new();
    // A disposed timer can still run a callback it already queued: the generation tells a stale one to do nothing.
    readonly Lock _patienceGate = new();
    int _patienceGeneration;
    bool _patienceStopped;
    readonly BehaviorSubject<(bool Finishing, string? Detail)> _progress = new((true, null));
    readonly TimeProvider _time;
    readonly ObservableCollection<UpdateStepViewModel> _steps = [];
    bool _settled;
    bool _clean;
    bool _handedOver;
    bool _disposed;
    readonly bool _closing;

    /// What the process being replaced shows from the moment the update is accepted until it exits: quitting
    /// first quiesces its agents' connections, which can take a while, and nothing else is on screen meanwhile.
    public static UpdateFinishViewModel Closing(string to, TimeProvider time) =>
        new(to, time, OperatingSystem.IsWindows());

    /// <param name="stopsDaemon">The installer stops the daemon with the app, as Velopack does on Windows.</param>
    internal UpdateFinishViewModel(string to, TimeProvider time, bool stopsDaemon) {
        _time = time;
        _closing = true;
        OpenCommand = ReactiveCommand.Create(() => { });
        SetSteps([
            stopsDaemon
                ? new("Closing kcap and the daemon", "Eligible agents resume if auto-resume is on", UpdateStepState.Running)
                : new("Closing kcap", "The daemon and its agents keep running", UpdateStepState.Running),
            new($"Installing kcap {Display(to)}", "Reopens when it is done", UpdateStepState.Pending),
        ]);
    }

    public UpdateFinishViewModel(
            AppUpdateTransition transition, IObservable<AttachStatus> status, IObservable<(string? Version, string? Connection)> daemon,
            IObservable<bool> restartPending, IObservable<LocalStepInput?> local, TimeProvider time) {
        _time = time;
        OpenCommand = ReactiveCommand.Create(RequestOpen);
        ArmPatience();
        local.Select(l => l?.Running == true).DistinctUntilChanged().Skip(1).Where(running => !running)
            .Subscribe(_ => ArmPatience())
            .DisposeWith(_disposables);

        status.StartWith(new AttachStatus(AttachState.Connecting, null, null)).CombineLatest(daemon.StartWith((null, null)), restartPending.StartWith(false), local.StartWith((LocalStepInput?)null),
                _waitedLong, (s, d, pending, l, waited) => Project(transition, s, d.Version, d.Connection, pending, l, waited))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Apply)
            .DisposeWith(_disposables);
    }

    public string Heading => _closing ? "Updating kcap…"
        : !_settled ? "Finishing the update…" : _clean ? "kcap is ready" : "kcap is installed";

    public string Description => _closing
        ? "kcap restarts to install the update. Background services finish updating after it reopens."
        : "Background services update automatically. Open kcap at any time. Progress stays visible in the app.";

    public bool CanOpen => !_closing;

    public IReadOnlyList<UpdateStepViewModel> Steps => _steps;

    public bool IsSettled => _settled;

    public string OpenLabel => "Open kcap";

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// Raised once, when the user goes on or every step is done.
    public event Action? Completed;

    /// Whether steps are still running, and the first of them in one line. Kept current after an early hand-over,
    /// so the main window can hold back what the update is still changing.
    public IObservable<(bool Finishing, string? Detail)> Progress => _progress.DistinctUntilChanged();

    /// Explicit handover, including while automatic completion is waiting for an earlier app to quit.
    public event Action? OpenRequested;

    void Apply((IReadOnlyList<UpdateStep> Steps, bool Settled, bool Clean) state) {
        if (_disposed) return;
        SetSteps(state.Steps);
        var cleanNow = state.Clean && !_clean;
        _settled = state.Settled;
        _clean = state.Clean;
        this.RaisePropertyChanged(nameof(Heading));
        this.RaisePropertyChanged(nameof(IsSettled));
        this.RaisePropertyChanged(nameof(OpenLabel));
        _progress.OnNext((!state.Settled, ProgressDetail(state.Steps)));
        if (cleanNow) _ = CloseAfterAsync();
    }

    void SetSteps(IReadOnlyList<UpdateStep> steps) {
        if (_steps.Count == 0) {
            foreach (var step in steps) _steps.Add(new(step));
            return;
        }

        // App and daemon are always present. Only the local server row can appear or disappear.
        // Retain each row's controls so progress changes do not restart their animations.
        if (_steps.Count < steps.Count) _steps.Insert(1, new(steps[1]));
        else if (_steps.Count > steps.Count) _steps.RemoveAt(1);
        for (var i = 0; i < steps.Count; i++) _steps[i].Step = steps[i];
    }

    void ArmPatience() {
        lock (_patienceGate) {
            if (_patienceStopped) return;
            var generation = ++_patienceGeneration;
            _waitedLong.OnNext(false);
            _patience.Disposable = _time.CreateTimer(_ => {
                lock (_patienceGate) {
                    if (!_patienceStopped && generation == _patienceGeneration) _waitedLong.OnNext(true);
                }
            }, null, DaemonPatience, Timeout.InfiniteTimeSpan);
        }
    }

    async Task CloseAfterAsync() {
        try {
            await Task.Delay(HandOverDelay, _time).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }
        RxSchedulers.MainThreadScheduler.Schedule(() => { if (_clean) Close(); });
    }

    internal static string? ProgressDetail(IReadOnlyList<UpdateStep> steps) =>
        steps.FirstOrDefault(step => step.State is UpdateStepState.Running or UpdateStepState.Pending) is { } step
            ? step.Activity ?? step.Detail ?? step.Title
            : null;

    void RequestOpen() {
        OpenRequested?.Invoke();
        Close();
    }

    void Close() {
        if (_handedOver) return;
        _handedOver = true;
        Completed?.Invoke();
    }

    internal static (IReadOnlyList<UpdateStep> Steps, bool Settled, bool Clean) Project(
            AppUpdateTransition transition, AttachStatus status, string? daemonVersion, string? connection, bool restartPending,
            LocalStepInput? local, bool waitedLong = false) {
        var steps = new List<UpdateStep> { new($"kcap {Display(transition.To)}", "Installed", UpdateStepState.Done) };
        var localStep = LocalStep(local);
        if (localStep is not null) steps.Add(localStep);
        var localRunning = localStep?.State is UpdateStepState.Running or UpdateStepState.Pending;
        steps.Add(DaemonStep(status, daemonVersion, connection, restartPending, transition.To, waitedLong, localRunning));
        var settled = steps.All(step => step.State is not (UpdateStepState.Running or UpdateStepState.Pending));
        return (steps, settled, settled && steps.All(step => step.State == UpdateStepState.Done));
    }

    /// <param name="connection">The daemon's connection to the server: the main window reads anything but
    /// connected as an outage, so the step is not done before it.</param>
    /// <param name="localRunning">The local server is still being upgraded. A daemon already on the new version waits for it
    /// rather than showing done and then dropping back when the server stops.</param>
    internal static UpdateStep DaemonStep(AttachStatus status, string? daemonVersion, string? connection, bool restartPending, string appVersion,
            bool waitedLong = false, bool localRunning = false) {
        const string title = "Daemon";
        var app = Display(appVersion);
        var known = daemonVersion is { Length: > 0 };
        var current = known && SameVersion(daemonVersion!, appVersion);
        var newer = known && PrereleaseSemver.IsNewer(Display(daemonVersion!), app);
        return status switch {
            { State: AttachState.Connected } when current && localRunning => new($"{title} {app}", "Waiting for the local server",
                UpdateStepState.Running, Activity: "Waiting for the local server"),
            { State: AttachState.Connected } when current && connection == ServerConnected => new($"{title} {app}", "Running", UpdateStepState.Done),
            { State: AttachState.Connected } when current && waitedLong => new($"{title} {app}", "Not connected to the server yet", UpdateStepState.Note),
            { State: AttachState.Connected } when current => new($"{title} {app}", "Connecting to the server", UpdateStepState.Running,
                Activity: "Reconnecting the daemon to the server"),
            { State: AttachState.Connected } when newer => new(title, $"Running {Display(daemonVersion!)}, newer than this app", UpdateStepState.Note),
            { State: AttachState.Connected } when restartPending => new(title, "Waiting for active turns. Long turns may be interrupted", UpdateStepState.Note),
            { State: AttachState.Connected } when waitedLong && known => new(title,
                $"Still on {Display(daemonVersion!)}. Moves to {app} on its next restart", UpdateStepState.Note),
            { State: AttachState.Connected } => new(title, $"Waiting to restart into {app}", UpdateStepState.Running, Activity: "Waiting for the daemon to restart"),
            { State: AttachState.Unreachable, Reason: ExpectedDaemonRestart.Reason } => new(title, $"Restarting into {app}", UpdateStepState.Running,
                Activity: "Restarting the daemon"),
            // The old daemon is incompatible with the new app until it restarts into the new binary.
            { State: AttachState.Unreachable, Reason: "daemon_incompatible" } when !waitedLong => new(title, $"Waiting to restart into {app}", UpdateStepState.Running,
                Activity: "Waiting for the daemon to restart"),
            { State: AttachState.Unreachable, Reason: "daemon_incompatible" } => new(title,
                "Still incompatible with this app. Open kcap and press Reconnect", UpdateStepState.Problem),
            { State: AttachState.Unreachable } => new(title, "Not running. Open kcap and press Start daemon", UpdateStepState.Problem),
            _ => new(title, "Connecting", UpdateStepState.Running, Activity: "Connecting to the daemon"),
        };
    }

    internal static UpdateStep? LocalStep(LocalStepInput? local) {
        if (local is null || local.Skipped) return null;
        const string title = "Local server";
        if (local.Running) {
            var heading = (local.From, local.To) switch {
                ({ } from, { } to) => $"{title} {from} → {to}",
                (_, { } to) => $"{title} {to}",
                _ => title,
            };
            var (detail, trailing, activity) = local.Phase switch {
                LocalUpgradePhase.Recovering => ("Finishing an interrupted update", null, "Finishing an interrupted local server update"),
                LocalUpgradePhase.Downloading => ("Downloading",
                    local.DownloadFraction is { } f ? $"{(int)Math.Round(f * 100)}%" : null, "Downloading the local server"),
                LocalUpgradePhase.DownloadingModel => ("Downloading the search model",
                    local.DownloadFraction is { } m ? $"{(int)Math.Round(m * 100)}%" : null, "Downloading the search model"),
                LocalUpgradePhase.Verifying => ("Verifying the download", null, "Verifying the local server download"),
                LocalUpgradePhase.Extracting => ("Extracting", null, "Extracting the local server"),
                LocalUpgradePhase.Stopping => ("Stopping the server", null, "Stopping the local server"),
                LocalUpgradePhase.Starting => ("Starting the server", null,
                    "Starting the local server. A migration can take a few minutes"),
                LocalUpgradePhase.Restarting => ($"Starting {local.From} again", (string?)null, "Starting the previous local server again"),
                _ => ("Checking the installation", null, "Checking the local server"),
            };
            return new(heading, detail, UpdateStepState.Running, trailing, Activity: activity);
        }
        var hint = string.Join(". ", new[] { local.Headline, local.Detail }.Where(part => !string.IsNullOrEmpty(part)).Select(part => Fragment(part!)));
        return local.Outcome switch {
            LocalUpgradeOutcome.Upgraded { SearchWarning: { } warning } upgraded => new(
                upgraded.To is { } version ? $"{title} {version}" : title,
                "Updated. Search uses keywords. Retry in Settings → Search", UpdateStepState.Note, Hint: warning),
            LocalUpgradeOutcome.Upgraded { To: { } to } => new($"{title} {to}", "Updated", UpdateStepState.Done),
            LocalUpgradeOutcome.Upgraded => new(title, "Updated", UpdateStepState.Done),
            LocalUpgradeOutcome.NothingToDo => new(title, "Up to date", UpdateStepState.Done),
            LocalUpgradeOutcome.NotPublished => new(title, (local.To, local.From) switch {
                ({ } to, { } from) => $"{to} is not available yet. Staying on {from}",
                ({ } to, _) => $"{to} is not available yet",
                _ => "The matching version is not available yet",
            }, UpdateStepState.Note, Hint: hint),
            LocalUpgradeOutcome.Failed { Switched: false, Affordance: LocalInstallAffordance.Busy } => new(title,
                "Waiting for another local setup to finish", UpdateStepState.Note, Hint: hint),
            LocalUpgradeOutcome.Failed { Switched: true } f => new(title,
                $"{f.To ?? "The new version"} did not start. Open kcap and press Start", UpdateStepState.Problem, Hint: f.Message),
            LocalUpgradeOutcome.Failed f => new(title,
                $"Update did not finish. { (f.From is { } stays ? $"Staying on {stays}. " : "") }{(f.Affordance == LocalInstallAffordance.Permanent ? "Open kcap for details" : "Open kcap and press Try again")}",
                UpdateStepState.Problem, Hint: f.Message),
            _ => new(title, "Not started", UpdateStepState.Pending, Activity: "Getting ready to update the local server"),
        };
    }

    /// A step's detail is a fragment: no closing full stop.
    static string Fragment(string text) => text.TrimEnd().TrimEnd('.');

    static bool SameVersion(string a, string b) => PrereleaseSemver.Compare(Display(a), Display(b)) == 0;

    static string Display(string version) => version.Split('+', 2)[0];

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
        lock (_patienceGate) {
            _patienceStopped = true;
            _patience.Dispose();
            _waitedLong.Dispose();
        }
        _progress.OnNext((false, null));
        _progress.OnCompleted();
        _progress.Dispose();
    }
}
