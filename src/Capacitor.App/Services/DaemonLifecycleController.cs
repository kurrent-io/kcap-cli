using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

/// Mirrors Capacitor.Cli.Services.VerifyExit (source of truth:
/// src/Capacitor.Cli/Services/ServiceVerify.cs) plus DaemonCommands' detached-start digest gate. The
/// app never references the CLI project, so the coded exits are duplicated here deliberately — the
/// one shared home for every coded daemon-mutation exit, reused by DaemonMutationLane's classifier.
internal static class VerifyExitCodes {
    public const int Contended           = 20;
    public const int Viability           = 21;
    public const int BootoutUnknown      = 22;
    public const int StopUnconfirmed     = 23;
    public const int ReadinessTimeout    = 24;
    public const int HelloValidation     = 25;
    public const int RollbackBudget      = 26;
    public const int RestoreVerification = 27;
    public const int StartGate           = 28;
    public const int StartGateDrift      = 29;
    public const int RetireRefused       = 30;
    public const int ElevationDeclined   = 1223; // Win32 ERROR_CANCELLED: the UAC prompt for the Windows service was declined
    public const int DigestGate          = 43; // not a VerifyExit code (DaemonCommands' own digest gate) — mapped in Token() below like every other coded exit

    public static string Token(int exitCode) => exitCode switch {
        ElevationDeclined   => "elevation_declined",
        Contended           => "verify_contended",
        Viability           => "verify_viability",
        BootoutUnknown      => "verify_bootout_unknown",
        StopUnconfirmed     => "verify_stop_unconfirmed",
        ReadinessTimeout    => "verify_readiness_timeout",
        HelloValidation     => "verify_hello_validation",
        RollbackBudget      => "verify_rollback_budget",
        RestoreVerification => "verify_restore_verification",
        StartGate           => "verify_start_gate",
        StartGateDrift      => "verify_start_gate_drift",
        RetireRefused       => "verify_retire_refused",
        DigestGate          => "daemon_start_gate",
        _                   => $"verify_unknown_{exitCode}",
    };
}

/// The state machine of the lifecycle slice: reacts to IDaemonClientService's attach stream,
/// drives the startup matrix through IKcapCli, and surfaces every inconsistency via
/// ILifecycleSurface — never a silent mutation outside the matrix's own explicit rows.
public sealed class DaemonLifecycleController : IAsyncDisposable {
    const string IncompatibleReason = "daemon_incompatible";

    /// Every unit rewrite the app offers carries this disclosure.
    internal const string TakeoverDisclosure =
        "kcap reinstalls the daemon service. Custom settings, including agent limits, reset. If installation fails, the service stays uninstalled.";

    /// Status when Start finds a live service and only kicks reattach. MainWindow replaces this
    /// (and its own Retry reconnect copy) if attach lands Unreachable again.
    internal const string AlreadyRunningReconnectStatus =
        "Daemon service is already running. Reconnecting…";

    internal const string RenameRestartStatus = "The daemon name changed. Restart this app before managing it.";

    internal const string UnsupervisedAttention =
        "The daemon was started manually. It will not restart automatically.";

    /// <summary>The move stops the daemon keeping its agents, so one that resumes them carries them over</summary>
    internal static string WindowsServiceDisclosure(int activeAgents, bool resumes) =>
        "The Windows service starts the daemon at sign-in and restarts it if it stops. " +
        "Administrator approval is required. The daemon keeps the same user account.\n\nThe daemon restarts now." +
        (activeAgents == 0 || !resumes ? ""
            : $" Its {activeAgents} running agent{(activeAgents == 1 ? " resumes" : "s resume")} if auto-resume is on.");

    internal const string MovingToWindowsServiceStatus = "Moving the daemon to the Windows service…";

    internal const string WindowsServiceUpdateDisclosure =
        "The Windows service needs an update. Administrator approval is required. The daemon and its agents keep running.";

    internal const string UnsupervisedDisclosure =
        "The service will replace this manually started daemon. Running agents will stop. The service will keep the new daemon running.";

    internal static readonly TimeSpan TxnActiveRequeryDelay = TimeSpan.FromSeconds(2);

    /// The shortest gap between two recoveries, so a daemon that dies straight after every start
    /// cannot turn the attach loop into a reinstall loop. The service manager's own restart policy
    /// covers a crash well inside it. It doubles with every recovery that no connect follows, up to
    /// <see cref="MaxRecoveryInterval"/>: each ensure kicks a reattach, whose failure is itself the
    /// next unreachable.
    internal static readonly TimeSpan RecoveryInterval    = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxRecoveryInterval = TimeSpan.FromMinutes(30);

    readonly IDaemonClientService _client;
    readonly IKcapCli _cli;
    readonly ILoginShellProbe _probe;
    readonly ILifecycleSurface _surface;
    readonly Func<Task<string?>> _resolveProfileName;
    readonly TimeProvider _time;
    // Fixed for the controller's lifetime, same as KcapCli's own canonicalServer (both resolved
    // once from the same resolution at composition time) — the mutation-request
    // guard (MutationRequestFactory) reads this at every lane call, never re-resolving it.
    readonly string? _canonicalServer;
    // The lane's RunAsync — every mutating branch routes through it instead of IKcapCli directly; this controller's own _gate serializes DECIDING, the lane serializes EXECUTION.
    readonly Func<MutationRequest, CancellationToken, Task<MutationOutcome>> _runMutation;
    // When true, RunStartupBranchAsync never admits; PhaseClosed still resolves, StartActionAsync unaffected.
    readonly bool _autoActionsPermanentlyClosed;
    readonly Func<bool> _requiresAppRestart;
    readonly Func<Task<bool>> _windowsServiceDeclined;
    readonly Func<Task> _rememberWindowsServiceDeclined;
    readonly Func<MutationRequest, CancellationToken, Task<string?>> _replacementCliVersion;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    readonly TaskCompletionSource<bool> _phaseClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _lock = new();

    internal const string StandardPriorityStatus = "Daemon runs at standard priority.";
    internal const string PromptStaleStatus      = "The daemon changed while the prompt was open. Nothing was changed.";
    internal const string PromptBusyStatus       = "Agents started while the prompt was open. Nothing was changed.";

    readonly BehaviorSubject<bool>         _backgroundPriority = new(false);
    readonly BehaviorSubject<ReloadState?> _reloadState        = new(null);
    readonly BehaviorSubject<bool>         _isReloading        = new(false);
    readonly BehaviorSubject<bool>         _isMovingToService  = new(false);
    IDisposable? _snapshots;
    DaemonInfoDto? _latestDaemon;
    string? _reportedMismatch; // under _lock: the identity mismatch last reported, so a reattach does not repeat it
    bool _mismatchWaitsForIdle; // under _lock: the last report was held back by running agents
    int  _latestActiveAgents;
    DaemonActivity? _latestActivity;
    readonly BehaviorSubject<string?> _identityReport = new(null);
    readonly BehaviorSubject<string?> _staleMarkerOffer = new(null);
    // Serializes the marker offer and its withdrawal, which status reads inside and outside _gate both drive.
    readonly Lock _markerLock = new();
    long _markerSeq;           // under _markerLock: start stamp of the newest read that spoke about the marker
    string? _staleMarkerShown; // under _markerLock: the exact line Attention received, so Withdraw can match it

    /// The attached daemon's latest reported activity; null until a snapshot arrives and whenever the attach drops.
    public DaemonActivity? LatestActivity => Volatile.Read(ref _latestActivity);

    /// The standing report about the attached daemon's identity, null while it matches. A state, not an event:
    /// a banner that resets on connect returns to it rather than losing it to however the two are ordered.
    public IObservable<string?> IdentityReport => _identityReport;

    /// The banner line <see cref="RepairStaleMarkerAsync"/> answers while a leftover transaction marker
    /// stands, else null. A Repair button shows only beside this exact line.
    public IObservable<string?> RepairOffer => _staleMarkerOffer.DistinctUntilChanged();
    // Running or starting, admitted launches included; an older daemon that sends no Busy falls back to the lists.
    volatile bool _latestBusy;
    bool _latestResumes;
    int  _reloadClaim;   // 0 free, 1 claimed; Interlocked only
    long _sequence;      // under _lock: both outcome records and read starts
    ReloadState? _standing; // under _lock: the authoritative reload state; the subject only publishes it
    bool _background;       // under _lock
    long _backgroundSeq;    // under _lock: the start stamp of the read _background reflects
    readonly Lock _publish = new();

    IDisposable? _subscription;
    bool _armClaimed;
    DateTimeOffset? _lastRecovery; // under _lock
    bool _recoveryPending;         // under _lock
    bool _supervisionOffered;      // under _lock: the unsupervised-daemon offer is made once per run
    bool _windowsServiceOffered;   // under _lock: the Windows service install or update is offered once per run
    bool _movingToWindowsService;  // under _lock: the moving line stays up until the daemon attaches again
    int _recoveriesSinceConnect;   // under _lock
    bool _disposed;
    int _generation;
    (AttachState State, string? Reason) _lastObserved = (AttachState.Connecting, null);
    string? _unreachableVersion; // under _lock: the hello reply's version on the last Unreachable

    public DaemonLifecycleController(
            IDaemonClientService client, IKcapCli cli, ILoginShellProbe probe,
            ILifecycleSurface surface, Func<Task<string?>> resolveProfileName, TimeProvider time,
            string? canonicalServer, Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            bool autoActionsPermanentlyClosed = false, Func<bool>? requiresAppRestart = null,
            Func<Task<bool>>? windowsServiceDeclined = null, Func<Task>? rememberWindowsServiceDeclined = null,
            Func<MutationRequest, CancellationToken, Task<string?>>? replacementCliVersion = null) {
        _client                        = client;
        _cli                           = cli;
        _probe                         = probe;
        _surface                       = surface;
        _resolveProfileName            = resolveProfileName;
        _time                          = time;
        _canonicalServer               = canonicalServer;
        _runMutation                   = runMutation;
        _autoActionsPermanentlyClosed  = autoActionsPermanentlyClosed;
        _requiresAppRestart             = requiresAppRestart ?? (() => false);
        _windowsServiceDeclined         = windowsServiceDeclined ?? (() => Task.FromResult(false));
        _rememberWindowsServiceDeclined = rememberWindowsServiceDeclined ?? (() => Task.CompletedTask);
        _replacementCliVersion          = replacementCliVersion ?? ((_, ct) => _cli.VersionAsync(ct));
    }

    /// Completes permanently on the first terminal attach outcome (Connected /
    /// daemon_incompatible / a completed daemon_unreachable ensure branch). Consumed by later
    /// tasks (shim offer timing).
    public Task PhaseClosed => _phaseClosed.Task;

    /// Cached once at Start(); null when the CLI is missing or --version failed.
    public string? CliVersion { get; private set; }

    /// The version of the daemon the last attach reached: its snapshot's while connected, the hello reply's after a
    /// failed attach, else null. The snapshot outlives a dropped attach, so it is never read once one fails.
    public string? AttachedDaemonVersion {
        get {
            lock (_lock) return _lastObserved.State switch {
                AttachState.Connected   => Volatile.Read(ref _latestDaemon)?.Version,
                AttachState.Unreachable => _unreachableVersion,
                _                       => null,
            };
        }
    }

    /// True while launchd holds the service in the background band; unchanged by an unknown word or a failed read.
    public IObservable<bool> BackgroundPriority => _backgroundPriority.DistinctUntilChanged();
    /// The last Reload outcome that still stands, or null. Replays to a late subscriber.
    public IObservable<ReloadState?> ReloadState => _reloadState;
    /// True from the claim before the prompt until the outcome is recorded and the follow-up read is done.
    public IObservable<bool> IsReloading => _isReloading.DistinctUntilChanged();

    /// <summary>From the move to the Windows service until the daemon attaches again: no daemon, on purpose</summary>
    public IObservable<bool> IsMovingToWindowsService => _isMovingToService.DistinctUntilChanged();

    /// The capability a daemon that keeps its agents for a successor advertises; one without it ends them on a reload.
    internal const string ResumeCapability = "shutdown/1";

    internal static string ReloadDisclosure(int activeAgents, bool resumes) => resumes
        ? "Reloading restarts the daemon now. " +
          (activeAgents == 0 ? ""
              : $"Its {activeAgents} running agent{(activeAgents == 1 ? " is" : "s are")} stopped, even mid-turn. " +
                "Eligible agents resume if auto-resume is on. ") +
          "A review or evaluation still running when it exits may end."
        : $"Reloading restarts the daemon and stops its {activeAgents} agent{(activeAgents == 1 ? "" : "s")}. " +
          "Any other agents, launches or evaluations running when it exits also end. Uncommitted work in their worktrees is lost.";

    bool ResumesAgents() {
        lock (_lock) return _latestResumes;
    }

    /// Subscribes to the attach stream. MUST be called before the host calls
    /// IDaemonClientService.Start() — the host owns pumping the attach loop; a subscription that
    /// starts after the pump has begun could miss the first terminal outcome the startup phase
    /// hinges on.
    public void Start() {
        _subscription = _client.Status.Subscribe(OnAttachStatus);
        _snapshots    = _client.Snapshots.Subscribe(snap => {
            _latestActiveAgents = snap.Daemon.ActiveAgents;
            var busy = snap.Busy ?? (snap.Daemon.ActiveAgents > 0 || snap.Pending is { Count: > 0 });
            _latestBusy = busy;
            Volatile.Write(ref _latestActivity, new DaemonActivity(snap.Daemon.ActiveAgents, busy));
            Volatile.Write(ref _latestDaemon, snap.Daemon);
            bool idleNow;
            lock (_lock) idleNow = _mismatchWaitsForIdle && !busy;
            if (idleNow) _ = CheckAttachedIdentityAsync();
        });
        _ = CacheVersionAsync(); // never faults
    }

    async Task CacheVersionAsync() {
        try {
            CliVersion = await _cli.VersionAsync(_lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown before the version probe returned — nothing to cache
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle version probe failed unexpectedly: {ex.Message}");
        }
    }

    // Every AttachStatus transition bumps the generation and records the last-observed outcome. The
    // FIRST terminal outcome (never Connecting) claims the startup arm; every later daemon_unreachable
    // is a daemon that went away, and gets the same ensure.
    void OnAttachStatus(AttachStatus status) {
        bool isFirstTerminalOutcome;

        if (status.State != AttachState.Connected) Volatile.Write(ref _latestActivity, null);

        lock (_lock) {
            _generation++;
            _lastObserved = (status.State, status.Reason);
            _unreachableVersion = status.State == AttachState.Unreachable ? status.DaemonVersion : null;
            if (status.State == AttachState.Connected) _latestResumes = status.Capabilities?.Contains(ResumeCapability) == true;
            if (status.State == AttachState.Connected) _recoveriesSinceConnect = 0;

            isFirstTerminalOutcome = status.State != AttachState.Connecting && !_armClaimed;
            if (isFirstTerminalOutcome) _armClaimed = true;
        }

        if (status.State == AttachState.Connected) {
            WithdrawMovingToWindowsService();
            _ = CheckAttachedIdentityAsync();
        }

        if (!isFirstTerminalOutcome) {
            // A later Connected is a daemon that came up after the arm, or came back after a reload
            // outside the app: read its spawn type, arm nothing.
            if (status.State == AttachState.Connected) _ = LaterConnectedAsync();
            else if (status.State == AttachState.Unreachable && status.Reason != IncompatibleReason
                     && !_autoActionsPermanentlyClosed)
                _ = RunRecoveryAsync((status.State, status.Reason));
            return;
        }

        switch (status.State) {
            case AttachState.Connected:
                ClosePhase();
                _ = RunReconciliationAsync(attached: true);
                break;
            case AttachState.Unreachable when status.Reason == IncompatibleReason:
                ClosePhase();
                _ = RunReconciliationAsync(attached: false);
                break;
            case AttachState.Unreachable:
                // Closed: PhaseClosed still resolves here — only the ensure matrix itself never admits.
                if (_autoActionsPermanentlyClosed) ClosePhase();
                else _ = RunStartupBranchAfterExpectedRestartAsync((status.State, status.Reason));
                break;
        }
    }

    /// Whether the daemon is restarting on its own, as <see cref="ExpectedDaemonRestart"/> judges it. An ensure
    /// then waits: its start would race the service manager that is already bringing the daemon back.
    public Func<bool> RestartExpected { get; set; } = () => false;

    async Task RunStartupBranchAfterExpectedRestartAsync((AttachState State, string? Reason) triggering) {
        if (!await WaitOutExpectedRestartAsync().ConfigureAwait(false)) {
            ClosePhase();
            return;
        }
        lock (_lock) _lastRecovery = _time.GetUtcNow();
        await RunStartupBranchAsync(triggering).ConfigureAwait(false);
    }

    /// False when the daemon came back while this waited, or the app is closing.
    async Task<bool> WaitOutExpectedRestartAsync() {
        try {
            while (RestartExpected()) await Task.Delay(TimeSpan.FromSeconds(1), _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return false;
        }
        lock (_lock) return _lastObserved.State == AttachState.Unreachable;
    }

    void ClosePhase() => _phaseClosed.TrySetResult(true);

    int CurrentGeneration() { lock (_lock) return _generation; }

    bool ObservedStatusChangedSince((AttachState State, string? Reason) triggering) {
        lock (_lock) return _lastObserved != triggering;
    }

    /// The freshest known attach state, read right before a Reconcile call rather than threaded
    /// through as a captured parameter across an async gap — a captured value can go stale (e.g.
    /// a racing Connected arriving mid-query) and silently disable the attached-only checks for
    /// the run's only reconciliation pass.
    bool IsCurrentlyAttached() { lock (_lock) return _lastObserved.State == AttachState.Connected; }

    async Task<bool> TryAcquireGateAsync(CancellationToken ct) {
        try {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        } catch (OperationCanceledException) {
            return false;
        }
    }

    enum QueryOutcome { Ok, Failed, Stale }

    /// Every service status read: stamped before it starts so a positive result can be ordered against a
    /// failure recorded while it was in flight, then fed to the indicator and the reload state.
    async Task<ServiceSnapshot?> ReadStatusAsync(CancellationToken ct) {
        long readStart;
        lock (_lock) readStart = ++_sequence;
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
        NoteSnapshot(snap, readStart);
        return snap;
    }

    /// The startup branch starting the daemon is what an update looks like, so this Connected is the run's only
    /// chance at the Windows service offer. That daemon connects while the branch still holds the gate inside its
    /// install transaction, where a status read can fail outright: on Windows a failed read is settled under the
    /// gate rather than taken as nothing to offer.
    async Task LaterConnectedAsync() {
        try {
            var passive = await ReadStatusAsync(_lifetime.Token).ConfigureAwait(false);
            if (passive is null ? !OffersSupervision() : !OffersWindowsService(passive)) return;
            lock (_lock) {
                if (_windowsServiceOffered) return;
            }
            if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
            try {
                if (IsCurrentlyAttached() && await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false) is { } snap)
                    await OfferWindowsServiceAsync(snap, _lifetime.Token).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle later-connect status read failed: {ex.Message}");
        }
    }

    void NoteSnapshot(ServiceSnapshot? snap, long readStart) {
        if (snap is null) return;
        NoteMarker(snap, readStart);
        var changedBackground = false;
        var clear = false;
        lock (_lock) {
            if (readStart <= _backgroundSeq) return; // reads overlap: one that started earlier and finished last carries older evidence
            var notLoaded = ServiceStateClassifier.Parse(snap.State) == ServiceState.NotInstalled;
            switch (SpawnTypes.Classify(snap.LoadedSpawnType)) {
                case SpawnTypeReading.BackgroundBand:
                    _backgroundSeq = readStart;
                    _background = true;
                    changedBackground = true;
                    break;
                case SpawnTypeReading.Positive:
                    _backgroundSeq = readStart;
                    _background = false;
                    changedBackground = true;
                    if (_standing is { } standing && ReloadCopy.ResolvedByPositivePriority(standing.Token) && standing.Sequence < readStart) {
                        _standing = null;
                        clear = true;
                    }
                    break;
                case SpawnTypeReading.Unknown when notLoaded: // nothing is loaded, so nothing runs in the band
                    _backgroundSeq = readStart;
                    _background = false;
                    changedBackground = true;
                    break;
            }
        }
        if (changedBackground) PublishBackground();
        if (clear) PublishReloadState();
    }

    // Each publisher reads its field inside the publish lock, so emissions can never invert.
    void PublishBackground() {
        lock (_publish) {
            bool current;
            lock (_lock) current = _background;
            _backgroundPriority.OnNext(current);
        }
    }

    void PublishReloadState() {
        lock (_publish) {
            ReloadState? current;
            lock (_lock) current = _standing;
            _reloadState.OnNext(current);
        }
    }

    async Task<(ServiceSnapshot? Snap, QueryOutcome Outcome)> QueryStatusAsync(CancellationToken ct) {
        var gen0 = CurrentGeneration();
        var snap = await ReadStatusAsync(ct).ConfigureAwait(false);
        if (CurrentGeneration() != gen0) return (null, QueryOutcome.Stale);
        return snap is null ? (null, QueryOutcome.Failed) : (snap, QueryOutcome.Ok);
    }

    /// General-purpose status query for reconciliation/requery/Start-action callers: one retry on
    /// stale evidence (never silently walks away with zero query), and a genuine CLI-level
    /// failure — on either attempt — surfaces an honest line and is logged.
    async Task<ServiceSnapshot?> QueryStatusForActionAsync(CancellationToken ct) {
        var (snap, outcome) = await QueryStatusAsync(ct).ConfigureAwait(false);
        if (outcome == QueryOutcome.Stale) (snap, outcome) = await QueryStatusAsync(ct).ConfigureAwait(false);

        if (outcome != QueryOutcome.Ok) {
            _surface.Status("Couldn't read the daemon service status, so kcap left it as it is.");
            Console.Error.WriteLine("kcap: daemon service status query failed, was unparseable, or never settled");
            return null;
        }

        return snap;
    }

    /// Startup-branch-specific query: retries once ONLY when the evidence is genuinely stale — a
    /// MEANINGFULLY different attach outcome (e.g. a racing Connected) arrived mid-query — never
    /// for a merely duplicate re-observation of the same outcome that triggered this branch. That
    /// distinction is what lets a duplicate daemon_unreachable stay a true no-op (the
    /// once-per-run arm) while a genuine race still gets re-evaluated instead of the branch
    /// silently walking away with zero reconciliation for the whole run.
    async Task<ServiceSnapshot?> QueryForStartupBranchAsync((AttachState State, string? Reason) triggering, CancellationToken ct) {
        var gen0 = CurrentGeneration();
        var snap = await ReadStatusAsync(ct).ConfigureAwait(false);

        if (CurrentGeneration() != gen0 && ObservedStatusChangedSince(triggering))
            snap = await ReadStatusAsync(ct).ConfigureAwait(false); // one re-evaluation against fresh state

        if (snap is null) {
            _surface.Status("Couldn't read the daemon service status, so kcap left it as it is.");
            Console.Error.WriteLine("kcap: daemon service status query failed or was unparseable during startup");
        }

        return snap;
    }

    async Task RunReconciliationAsync(bool attached) {
        if (_cli.CliPath is null) return;
        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;

        try {
            var snap = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (snap is not null) {
                Reconcile(snap, attached, allowTxnActiveRequery: true);
                if (attached && OffersSupervision() && IsUnsupervised(snap)) await OfferSupervisionAsync(snap, _lifetime.Token).ConfigureAwait(false);
                else if (attached) await OfferWindowsServiceAsync(snap, _lifetime.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // shutdown mid-query
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle reconciliation failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    /// A daemon that went away mid-run: the same ensure as at startup, at most once per
    /// <see cref="CurrentRecoveryInterval"/>. One inside the interval is deferred to its end rather
    /// than dropped, because the attach loop reports unreachable once per transition, not per retry.
    async Task RunRecoveryAsync((AttachState State, string? Reason) triggering) {
        TimeSpan wait;
        lock (_lock) {
            if (_recoveryPending) return;
            _recoveryPending = true;
            var interval = CurrentRecoveryInterval(_recoveriesSinceConnect);
            var since    = _lastRecovery is { } last ? _time.GetUtcNow() - last : interval;
            wait = since >= interval ? TimeSpan.Zero : interval - since;
        }

        try {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            lock (_lock) _recoveryPending = false;
            return;
        }
        if (!await WaitOutExpectedRestartAsync().ConfigureAwait(false)) {
            lock (_lock) _recoveryPending = false;
            return;
        }

        lock (_lock) {
            _recoveryPending = false;
            // Came back on its own (a service manager restart) while this waited, or came back too
            // new or too old for this app, which no ensure fixes.
            if (_lastObserved.State != AttachState.Unreachable || _lastObserved.Reason == IncompatibleReason) return;
            _lastRecovery = _time.GetUtcNow();
            _recoveriesSinceConnect++;
        }

        await RunStartupBranchAsync(triggering).ConfigureAwait(false);
    }

    internal static TimeSpan CurrentRecoveryInterval(int recoveriesSinceConnect) {
        var doubled = RecoveryInterval * Math.Pow(2, Math.Min(recoveriesSinceConnect, 16));
        return doubled < MaxRecoveryInterval ? doubled : MaxRecoveryInterval;
    }

    /// The ensure branch, at startup and on every recovery. Closes the startup phase only once this
    /// full flow — query, optional txn-active wait, matrix decision, any mutation, and its
    /// confirmation wait — has completed.
    async Task RunStartupBranchAsync((AttachState State, string? Reason) triggering) {
        try {
            if (_cli.CliPath is null) {
                _surface.Status("The kcap command-line tool wasn't found, so the app can't manage the daemon.");
                return;
            }

            if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
            try {
                var snap = await QueryForStartupBranchAsync(triggering, _lifetime.Token).ConfigureAwait(false);
                if (snap is null) return; // query failure/unknown — already surfaced above, no mutation

                // The true CURRENT attach state, not the (possibly stale) reason this branch was
                // triggered by: when QueryForStartupBranchAsync had to re-evaluate against a
                // racing Connected, this run's only reconciliation pass must not silently run in
                // permanently-unattached mode — that would make the attached-only checks
                // (ownership mismatch, coexistence) unreachable for the whole run.
                Reconcile(snap, IsCurrentlyAttached(), allowTxnActiveRequery: false);

                if (snap.TxnActive) {
                    // A held flock is waited out, never mutated into. One bounded
                    // re-query (not offered as repair); still active afterward → no action this
                    // run rather than risk contending the CLI's own transaction lock.
                    snap = await AwaitOneTxnActiveRequeryAsync(_lifetime.Token).ConfigureAwait(false);
                    if (snap is null || snap.TxnActive) return;
                }

                // A daemon that attached during the reads above needs nothing, whatever they saw.
                if (IsCurrentlyAttached()) return;

                await RunStartupMatrixAsync(snap, _lifetime.Token).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown mid-branch
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle startup branch failed unexpectedly: {ex.Message}");
        } finally {
            ClosePhase();
        }
    }

    /// The startup matrix, keyed on the loaded-label/job state before plist presence. An unrecognized wire
    /// state is Unknown, not NotInstalled — positive evidence only, never a silent entry into the
    /// auto-install/start path below.
    async Task RunStartupMatrixAsync(ServiceSnapshot snap, CancellationToken ct) {
        var state = ServiceStateClassifier.Parse(snap.State);
        if (state == ServiceState.Unknown) {
            _surface.Status("The daemon service is in an unknown state, so kcap left it as it is.");
            Console.Error.WriteLine($"kcap: daemon lifecycle startup matrix saw an unrecognized service state: {snap.State}");
            return;
        }

        if (state == ServiceState.Running) return; // launchd's own backoff keeps retrying

        if (state == ServiceState.Installed) { // loaded, inactive
            if (!snap.UnitPresent) {
                // A live pid could be another identity's daemon on this name, and a replace would kill it.
                if (snap.DaemonPid is not null) {
                    _surface.Attention("The daemon service is loaded, but its unit file is missing. It needs a repair.");
                    return;
                }

                // Our own service, broken: repaired without asking, as setup would.
                var missing = await FailingRepairPreconditionAsync(snap).ConfigureAwait(false);
                if (missing is not null) _surface.Status(missing);
                else await RunLaneMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(false);
                return;
            }
            if (snap.DaemonPid is not null) {
                AttentionCoexistence(snap.DaemonPid.Value);
                return;
            }
            await RunLaneMutationAsync(MutationVerb.StartVerified, ct).ConfigureAwait(false);
            return;
        }

        // state == ServiceState.NotInstalled: no loaded label.
        if (snap.UnitPresent) {
            if (snap.DaemonPid is not null) {
                AttentionCoexistence(snap.DaemonPid.Value);
                return;
            }
            await RunLaneMutationAsync(MutationVerb.StartVerified, ct).ConfigureAwait(false);
            return;
        }

        var failure = await FailingPreconditionAsync(snap, ct).ConfigureAwait(false);
        if (failure is not null) {
            _surface.Status(failure);
            return;
        }

        // No DaemonPid check here, unlike the start rows above: a racing/wedged manual daemon on
        // this name is the install --verify transaction's own job to detect and safely roll back
        // from (post-install ownership + hello verification) — not a
        // pre-flight guess by the app.
        await RunLaneMutationAsync(MutationVerb.Install, ct).ConfigureAwait(false);
    }

    void AttentionCoexistence(int daemonPid) =>
        _surface.Attention(
            $"A daemon (PID {daemonPid}) is already running beside the installed service, so kcap won't start a second one.");

    /// Install-only preconditions — start performs no viability check, so this
    /// is never called on a start row. Returns the honest line to surface on failure, or null
    /// once every precondition passes.
    async Task<string?> FailingPreconditionAsync(ServiceSnapshot snap, CancellationToken ct) {
        if (snap.InstallBinaryPath is null)
            return "kcap can't find its own daemon binary, so it skipped the automatic install.";

        var profile = await _resolveProfileName().ConfigureAwait(false);
        if (profile is null)
            return "No profile has a valid server URL, so kcap skipped the automatic install.";

        var terminalPath = await _probe.TerminalPathAsync(ct).ConfigureAwait(false);
        if (terminalPath is null)
            return "kcap couldn't read your terminal PATH, so it skipped the automatic install.";

        return null;
    }

    bool RequireAppRestart() {
        if (!_requiresAppRestart()) return false;
        _surface.Status(RenameRestartStatus);
        return true;
    }

    /// Routes execution through the lane, then kicks reattach unless the graph requires a restart.
    async Task<bool> RunLaneMutationAsync(MutationVerb verb, CancellationToken ct) =>
        await RunLaneMutationOutcomeAsync(verb, ct).ConfigureAwait(false) is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout;

    /// <returns>Null when the app must restart first, before or after the attempt</returns>
    async Task<MutationOutcome?> RunLaneMutationOutcomeAsync(MutationVerb verb, CancellationToken ct) {
        if (RequireAppRestart()) return null;
        var profileName = await _resolveProfileName().ConfigureAwait(false);
        var refusal = MutationRequestFactory.TryBuild(verb, profileName, _canonicalServer, _client.DaemonName, out var request);
        if (refusal is MutationOutcome.Refused(var guardReason, _)) {
            _surface.Status($"kcap: {guardReason}");
            return refusal;
        }

        var outcome = await _runMutation(request!, ct).ConfigureAwait(false);
        if (RequireAppRestart()) return null;
        _ = _client.RestartLoopAsync(); // any mutation attempt may have restarted the daemon; kicking reattach is idempotent
        return outcome;
    }

    /// The ONE accept path for a unit rewrite this controller offers, so a future dialog can never
    /// grow a second `MutationVerb.Replace` call site. A consent that outlived an attach transition
    /// is discarded: the evidence the dialog disclosed may no longer hold.
    /// <param name="requireIdle">Refuse an accepted consent if agents started while the prompt was open:
    /// snapshots do not move the attach generation, so the stale-consent check cannot see them.</param>
    /// <param name="stillEligible">Re-checks, after consent, evidence the attach generation does not track.</param>
    async Task ConfirmAndReplaceAsync(
            LifecyclePrompt prompt, CancellationToken ct, bool requireIdle = false,
            Func<CancellationToken, Task<bool>>? stillEligible = null) {
        if (RequireAppRestart()) return;
        var target = await ReplacementCliVersionAsync(ct).ConfigureAwait(false);
        prompt = WithVersionChange(prompt, AttachedDaemonVersion, target);
        var gen0     = CurrentGeneration(); // captured immediately before ConfirmAsync (stale-consent check below)
        var accepted = await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(false);
        if (!accepted || RequireAppRestart()) return;

        if (CurrentGeneration() != gen0) {
            _surface.Status("The daemon changed while the prompt was open. Nothing was changed.");
            return;
        }

        if (requireIdle && _latestBusy) {
            _surface.Status(PromptBusyStatus);
            // Offered again once they finish; the flag goes first so an idle snapshot landing now still finds it.
            lock (_lock) _mismatchWaitsForIdle = true;
            if (!_latestBusy) _ = CheckAttachedIdentityAsync();
            return;
        }

        if (stillEligible is not null && !await stillEligible(ct).ConfigureAwait(false)) {
            _surface.Status(PromptStaleStatus);
            return;
        }

        await RunLaneMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(false);
    }

    /// The kcap the lane would run the replace with, probed now: the cached <see cref="CliVersion"/> can predate it.
    async Task<string?> ReplacementCliVersionAsync(CancellationToken ct) {
        var profileName = await _resolveProfileName().ConfigureAwait(false);
        return MutationRequestFactory.TryBuild(MutationVerb.Replace, profileName, _canonicalServer, _client.DaemonName, out var request) is null
            ? await _replacementCliVersion(request!, ct).ConfigureAwait(false)
            : null;
    }

    /// The replace installs the app's own kcap, so the prompt says which version that moves the daemon to.
    internal static LifecyclePrompt WithVersionChange(LifecyclePrompt prompt, string? daemonVersion, string? cliVersion) =>
        DaemonVersionChange.Describe(daemonVersion, cliVersion) is { } change
            ? prompt with { Disclosure = $"{prompt.Disclosure}\n\n{change}" }
            : prompt;

    /// The rail's Reload button. One operation at a time, claimed before the prompt: the confirmation
    /// surface queues dialogs and releases its gate when one is answered, so a claim taken on acceptance
    /// would let a second queued prompt be accepted while the first reload is still running.
    public async Task ReloadServiceAsync(CancellationToken ct) {
        if (Interlocked.CompareExchange(ref _reloadClaim, 1, 0) != 0) return;
        _isReloading.OnNext(true);
        try {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var token = linked.Token;
            if (RequireAppRestart()) return;
            var gen0   = CurrentGeneration();
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindReloadService, null, CliVersion, false, ReloadDisclosure(_latestActiveAgents, ResumesAgents()));
            var accepted = await _surface.ConfirmAsync(prompt, token).ConfigureAwait(false);
            if (!accepted || RequireAppRestart()) return;

            // From consent on, the mutation and its follow-up read hold the gate like every other mutating
            // branch, so disposal waits for them and the startup matrix never runs beside them.
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try {
                // The prompt and the gate wait are both windows an attach transition can land in, and the
                // consent was for the daemon the prompt described.
                if (CurrentGeneration() != gen0) {
                    _surface.Status(PromptStaleStatus);
                    return;
                }

                var profileName = await _resolveProfileName().ConfigureAwait(false);
                var refusal = MutationRequestFactory.TryBuild(MutationVerb.Reload, profileName, _canonicalServer, _client.DaemonName, out var request);
                var outcome = refusal ?? await _runMutation(request!, token).ConfigureAwait(false);
                Record(outcome);
                if (RequireAppRestart()) return;
                if (refusal is null) _ = _client.RestartLoopAsync(); // the mutation may have restarted the daemon; reattach is idempotent
                if (outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout) _surface.Status(StandardPriorityStatus);

                await ReadStatusAsync(token).ConfigureAwait(false); // the indicator follows evidence, not the click
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown or caller cancel: nothing left to render
        } finally {
            Volatile.Write(ref _reloadClaim, 0);
            _isReloading.OnNext(false);
        }
    }

    void Record(MutationOutcome outcome) {
        lock (_lock) _standing = Services.ReloadState.From(outcome, _client.DaemonName, ++_sequence);
        PublishReloadState();
    }

    internal static string StaleMarkerMessage(string daemonName, ServiceSnapshot snap) {
        var what  = snap.TxnOperation is { } op ? $"service {op}" : "service install";
        var where = snap.TxnPhase is { } phase ? $" at '{phase}'" : "";
        var when  = snap.TxnMarkedAt is { } at ? $" on {at.ToLocalTime():MMM d, HH:mm}" : "";
        return $"An earlier daemon {what} for '{daemonName}' did not finish: it stopped{where}{when}. " +
               "Repair reinstalls the daemon service and restarts the daemon.";
    }

    internal static string? RepairInterruptsWarning(int activeAgents) => activeAgents == 0
        ? null
        : $"{activeAgents} running agent{(activeAgents == 1 ? "" : "s")} on this daemon may be interrupted.";

    /// The stale-marker offer follows every status read, passive ones included, since a later attach
    /// only reads passively. Reads overlap, so the one that started last decides; a marker an operation
    /// still holds is left as it stands.
    void NoteMarker(ServiceSnapshot snap, long readStart) {
        lock (_markerLock) {
            if (readStart < _markerSeq) return;
            _markerSeq = readStart;

            if (snap.TxnMarker) {
                if (snap.TxnActive) return;
                var message = StaleMarkerMessage(_client.DaemonName, snap);
                _staleMarkerShown = message;
                _staleMarkerOffer.OnNext(message);
                _surface.Attention(message);
            } else if (_staleMarkerShown is { } shown) {
                _staleMarkerShown = null;
                _staleMarkerOffer.OnNext(null);
                _surface.Withdraw(shown);
            }
        }
    }

    /// The banner's Repair button: a confirmed reinstall, whose transaction recovers the leftover
    /// marker. Caller-awaited from the UI, so every path is exception-safe.
    public async Task RepairStaleMarkerAsync(CancellationToken ct) {
        try {
            if (RequireAppRestart() || _cli.CliPath is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var lct = linked.Token;

            if (!await TryAcquireGateAsync(lct).ConfigureAwait(false)) return;
            try {
                var snap = await QueryStatusForActionAsync(lct).ConfigureAwait(false);
                if (RequireAppRestart() || snap is null) return;

                if (snap.TxnMarker && !snap.TxnActive) {
                    var missing = await FailingRepairPreconditionAsync(snap).ConfigureAwait(false);
                    if (missing is not null) {
                        _surface.Status(missing);
                        return;
                    }

                    var terminalPath = await _probe.TerminalPathAsync(lct).ConfigureAwait(false);
                    var prompt = new LifecyclePrompt(
                        LifecyclePrompt.KindRepair, null, null, terminalPath is null, TakeoverDisclosure,
                        RepairInterruptsWarning(LatestActivity?.ActiveAgents ?? 0));
                    // The marker can be settled while the prompt is open, which moves no attach generation.
                    await ConfirmAndReplaceAsync(prompt, lct, stillEligible: async token =>
                            await QueryStatusForActionAsync(token).ConfigureAwait(false) is { TxnMarker: true, TxnActive: false })
                        .ConfigureAwait(false);

                    snap = await QueryStatusForActionAsync(lct).ConfigureAwait(false);
                    if (snap is null) return;
                }

                Reconcile(snap, IsCurrentlyAttached(), allowTxnActiveRequery: false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown or caller cancel
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon service repair failed unexpectedly: {ex.Message}");
        }
    }

    /// Preconditions every unit rewrite needs short of the silent install's terminal PATH: a repair,
    /// and an install the user asked for, proceed without it. A missing install binary or an
    /// unresolvable profile would make the rewrite a guaranteed coded viability failure.
    async Task<string?> FailingRepairPreconditionAsync(ServiceSnapshot snap) {
        if (snap.InstallBinaryPath is null)
            return "kcap can't find its own daemon binary, so the daemon service was left as it is.";

        var profile = await _resolveProfileName().ConfigureAwait(false);
        if (profile is null)
            return "No profile has a valid server URL, so the daemon service was left as it is.";

        return null;
    }

    /// Reconciliation: surfaces every inconsistent combination found in one
    /// ServiceStatusAsync snapshot — never mutates. The attached-only checks only make sense (or
    /// would otherwise double-report a startup-matrix row's own Attention for the exact same
    /// evidence) while genuinely connected.
    void Reconcile(ServiceSnapshot snap, bool attached, bool allowTxnActiveRequery) {
        var state = ServiceStateClassifier.Parse(snap.State);

        if (attached) {
            if (snap.JobPid is not null && snap.DaemonPid is not null && snap.JobPid != snap.DaemonPid)
                _surface.Attention(
                    $"The daemon service job (PID {snap.JobPid}) does not match the attached daemon (PID {snap.DaemonPid}).");
            else if (state == ServiceState.Running && snap.DaemonPid is null)
                _surface.Attention("The service reports its job running, but no attached-daemon evidence backs it.");
        }

        if (snap.TxnActive && allowTxnActiveRequery)
            _ = RunTxnActiveRequeryAsync();

        if (attached && snap.UnitPresent && state == ServiceState.NotInstalled && snap.DaemonPid is not null)
            _surface.Attention(
                $"A daemon (PID {snap.DaemonPid}) is running outside the installed service, which is stopped.");
    }

    // The inline, AWAITED building block: used by RunStartupBranchAsync, which already holds the
    // gate for its whole decision, to both wait out the delay AND gate the matrix's eligibility
    // on the result — no mutation while the flock is (or was very recently) held. `attached` is
    // read fresh (IsCurrentlyAttached) right before reconciling, not captured beforehand — the
    // delay is itself a window a race can land in.
    async Task<ServiceSnapshot?> AwaitOneTxnActiveRequeryAsync(CancellationToken ct) {
        await Task.Delay(TxnActiveRequeryDelay, _time, ct).ConfigureAwait(false);
        var fresh = await QueryStatusForActionAsync(ct).ConfigureAwait(false);
        if (fresh is not null) Reconcile(fresh, IsCurrentlyAttached(), allowTxnActiveRequery: false);
        return fresh;
    }

    // The fire-and-forget building block: scheduled from Reconcile's own txn-active branch
    // (Connected/incompatible reconciliation-only paths, which don't otherwise hold the gate for
    // this). The delay itself runs UNGATED — a passive background check must never block a user's
    // Start click for the whole wait — only the query+reconcile is gate-scoped, exactly like
    // every other mutation-adjacent evidence read. Exactly one follow-up query
    // (allowTxnActiveRequery: false on the way back in) — an orphaned grandchild that outlives a
    // force-quit is waited out, not repaired. Same freshest-read rule as above:
    // `attached` comes from IsCurrentlyAttached() at reconcile time, never a captured parameter.
    async Task RunTxnActiveRequeryAsync() {
        try {
            await Task.Delay(TxnActiveRequeryDelay, _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
        try {
            var fresh = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (fresh is not null) Reconcile(fresh, IsCurrentlyAttached(), allowTxnActiveRequery: false);
        } catch (OperationCanceledException) {
            // shutdown mid-requery
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle txn-active requery failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    /// The Start action: the ensure matrix on demand, always ending in the service, never a daemon
    /// outside it. Only a manual daemon holding the name is replaced on the user's word
    /// (OfferRepairAsync). Public + caller-awaited from the UI, so every path here is exception-safe
    /// — a click must never crash the app.
    public async Task StartActionAsync(CancellationToken ct) {
        try {
            if (RequireAppRestart()) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var lct = linked.Token;

            if (_cli.CliPath is null) {
                _surface.Status("The kcap command-line tool wasn't found, so the daemon can't start.");
                return;
            }

            if (!await TryAcquireGateAsync(lct).ConfigureAwait(false)) {
                _surface.Status("Couldn't start the daemon. Try again.");
                return;
            }
            try {
                // Fresh evidence every call — a Start racing an in-flight mutation blocks on the
                // gate above and, once it clears, re-queries rather than acting on anything it
                // might have observed before the wait.
                if (RequireAppRestart()) return;
                var snap = await QueryStatusForActionAsync(lct).ConfigureAwait(false);
                if (RequireAppRestart() || snap is null) return; // unknown — already surfaced, no action

                var state = ServiceStateClassifier.Parse(snap.State);
                if (state == ServiceState.Unknown) {
                    _surface.Status("The daemon service is in an unknown state, so kcap didn't start it.");
                    Console.Error.WriteLine($"kcap: daemon lifecycle start action saw an unrecognized service state: {snap.State}");
                    return;
                }

                if (state == ServiceState.Running) {
                    _surface.Status(AlreadyRunningReconnectStatus);
                    _ = _client.RestartLoopAsync();
                    return;
                }

                if (state == ServiceState.Installed) {
                    if (snap.DaemonPid is not null)
                        await OfferRepairAsync(snap, lct).ConfigureAwait(false); // coexistence
                    else if (snap.UnitPresent)
                        await ReportStartMutationAsync(MutationVerb.StartVerified, lct).ConfigureAwait(false);
                    else
                        await ReportUnitRewriteAsync(snap, MutationVerb.Replace, lct).ConfigureAwait(false); // orphan label
                    return;
                }

                // state == ServiceState.NotInstalled: no loaded label.
                if (snap.UnitPresent) {
                    if (snap.DaemonPid is null)
                        await ReportStartMutationAsync(MutationVerb.StartVerified, lct).ConfigureAwait(false); // bootstrap the stopped unit
                    else
                        await OfferRepairAsync(snap, lct).ConfigureAwait(false); // a manual daemon owns the name
                    return;
                }

                await ReportUnitRewriteAsync(snap, MutationVerb.Install, lct).ConfigureAwait(false); // nothing at all
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // caller-cancelled or shutting down — nothing to surface
        } catch (Exception ex) {
            _surface.Status("Starting the daemon failed unexpectedly.");
            Console.Error.WriteLine($"kcap: daemon lifecycle start action failed unexpectedly: {ex.Message}");
        }
    }

    /// Start-click feedback after a lane mutation: success and failure both write Status so the
    /// launcher banner is never left on a mute "Starting…" with no follow-up. Failure detail may
    /// also arrive later via Attention (outcome consumer); that overwrites this one-liner.
    async Task ReportStartMutationAsync(MutationVerb verb, CancellationToken ct) {
        var ok = await RunLaneMutationAsync(verb, ct).ConfigureAwait(false);
        if (RequireAppRestart()) return;
        _surface.Status(ok
            ? "Starting the daemon. Waiting for it to connect…"
            : "The daemon didn't start. Press Start daemon to try again.");
    }

    async Task ReportUnitRewriteAsync(ServiceSnapshot snap, MutationVerb verb, CancellationToken ct) {
        var missing = await FailingRepairPreconditionAsync(snap).ConfigureAwait(false);
        if (missing is not null) {
            _surface.Status(missing);
            return;
        }

        await ReportStartMutationAsync(verb, ct).ConfigureAwait(false);
    }

    /// The app attaches by daemon name, so the daemon behind it can be serving another server or profile
    /// than the app expects. The expected identity is the
    /// graph's own resolution, never a re-read of the active profile, so an external selection change
    /// cannot move the daemon out from under the app.
    /// <param name="reoffer">An offer superseded by a reconnect: the same mismatch is offered again, not reported again.</param>
    async Task CheckAttachedIdentityAsync(bool reoffer = false) {
        try {
            var generation = CurrentGeneration();
            if (Volatile.Read(ref _latestDaemon) is not { } daemon) return;
            var expectedProfile = await _resolveProfileName().ConfigureAwait(false);
            var mismatch = AttachedIdentity.Mismatch(daemon, _client.DaemonName, _canonicalServer, expectedProfile);
            var busy     = _latestBusy;
            // Withdrawn on every match, ahead of the de-duplication below, so no path leaves a stale report standing.
            if (mismatch is null && _identityReport.Value is { } stale) {
                _identityReport.OnNext(null);
                _surface.Withdraw(stale);
            }
            bool reported;
            lock (_lock) {
                reported = mismatch == _reportedMismatch;
                // The same mismatch is reported again only when the agents that held back its offer have finished.
                if (reported && !reoffer && !(_mismatchWaitsForIdle && !busy)) return;
                _reportedMismatch     = mismatch;
                _mismatchWaitsForIdle = false;
            }
            if (mismatch is null) return;
            var announce = !(reoffer && reported);

            if (expectedProfile is null || _autoActionsPermanentlyClosed || !AttachedIdentity.SupportsVerifiedReplace) {
                if (announce) Report($"{mismatch} {AttachedIdentity.ManualRepair(_client.DaemonName, expectedProfile)}");
                return;
            }
            if (busy) {
                var agents = daemon.ActiveAgents switch {
                    0 => "the agent it is starting is running and has finished",
                    1 => "its running agent finishes",
                    var n => $"its {n} running agents finish"
                };
                if (announce) Report($"{mismatch} The app offers to replace it once {agents}.");
                // After the report, so an idle outcome is the one left standing. An idle snapshot that landed before
                // the flag was set found nothing to wake, so this looks again; one landing after it does the looking.
                bool idleNow;
                lock (_lock) {
                    _mismatchWaitsForIdle = true;
                    idleNow = !_latestBusy;
                }
                if (idleNow) _ = CheckAttachedIdentityAsync();
                return;
            }

            // The command rides along, so a declined or failed replace still leaves the user a fix.
            if (announce) Report($"{mismatch} {AttachedIdentity.ManualRepair(_client.DaemonName, expectedProfile)}");
            // Under the gate like every other repair, so it neither races the reconciliation's own offers
            // nor escapes the shutdown barrier.
            if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
            var superseded = false;
            try {
                superseded = CurrentGeneration() != generation;
                if (!superseded && await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false) is { } snap)
                    await OfferRepairAsync(snap, _lifetime.Token, requireIdle: true).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
            // A reconnect while this waited was suppressed as already reported: offer it again on the attach now
            // current. Detached, the next attach reports afresh.
            if (superseded) {
                bool offerAgain;
                // One lock for all of it, so an attach landing between them cannot be suppressed by the mismatch about
                // to clear. A reconnect whose own check recorded a different mismatch has made its own offer.
                lock (_lock) {
                    var attached = _lastObserved.State == AttachState.Connected;
                    offerAgain = attached && _reportedMismatch == mismatch;
                    if (!attached && _reportedMismatch == mismatch) _reportedMismatch = null;
                }
                if (offerAgain) _ = CheckAttachedIdentityAsync(reoffer: true);
            }
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon identity check failed unexpectedly: {ex}");
        }
    }

    void Report(string message) {
        _identityReport.OnNext(message);
        _surface.Attention(message);
    }

    /// The Start button's one dialog: a manual daemon holds the name, and replacing it is the user's call.
    /// No service and no unit, yet a daemon answers: started by hand, so nothing brings it back. The
    /// startup matrix never sees this row, because it only runs when no daemon answers.
    internal static bool IsUnsupervised(ServiceSnapshot snap) =>
        ServiceStateClassifier.Parse(snap.State) == ServiceState.NotInstalled && !snap.UnitPresent && snap.DaemonPid is not null;

    /// <summary>Windows only: the takeover it leads to ends a standalone owner only on the Windows CLI</summary>
    internal Func<bool> OffersSupervision { get; init; } = OperatingSystem.IsWindows;

    /// Asked while the daemon is idle only, since the replacement ends what it hosts; a busy daemon
    /// keeps the attention line and is offered again on the next run.
    async Task OfferSupervisionAsync(ServiceSnapshot snap, CancellationToken ct) {
        lock (_lock) {
            if (_supervisionOffered) return;
            _supervisionOffered = true;
        }

        _surface.Attention(UnsupervisedAttention);
        if (_latestBusy || await FailingRepairPreconditionAsync(snap).ConfigureAwait(false) is not null) return;

        var terminalPath = await _probe.TerminalPathAsync(ct).ConfigureAwait(false);
        var prompt = new LifecyclePrompt(LifecyclePrompt.KindTakeover, null, CliVersion, terminalPath is null, UnsupervisedDisclosure);
        await ConfirmAndReplaceAsync(prompt, ct, requireIdle: true).ConfigureAwait(false);

        if (await QueryStatusForActionAsync(ct).ConfigureAwait(false) is { } after && !IsUnsupervised(after))
            _surface.Withdraw(UnsupervisedAttention);
    }

    /// <summary>The task supervising a daemon the bundled Windows service could take over, or an installed
    /// service older than the bundled one. Asked once per run; a declined install is remembered and not asked
    /// again, leaving the explicit action. The update leaves the daemon running, and the move resumes its
    /// agents, so only a busy daemon that cannot resume them waits, without spending the run's offer</summary>
    async Task OfferWindowsServiceAsync(ServiceSnapshot snap, CancellationToken ct) {
        if (!OffersWindowsService(snap)) return;
        var install = snap.Supervisor == "windows_scheduled_task";
        var resumes = ResumesAgents();
        if (install && _latestBusy && !resumes) return;

        lock (_lock) {
            if (_windowsServiceOffered) return;
            _windowsServiceOffered = true;
        }
        if (install && await _windowsServiceDeclined().ConfigureAwait(false)) return;

        var prompt = new LifecyclePrompt(
            install ? LifecyclePrompt.KindWindowsService : LifecyclePrompt.KindWindowsServiceUpdate, null, CliVersion, false,
            install ? WindowsServiceDisclosure(_latestActiveAgents, resumes) : WindowsServiceUpdateDisclosure);
        var gen0 = CurrentGeneration();
        if (!await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(false)) {
            // A prompt closed by the app shutting down was not answered
            if (install && !ct.IsCancellationRequested) await _rememberWindowsServiceDeclined().ConfigureAwait(false);
            return;
        }
        // The consent was for the daemon the prompt described, ending none of the work started since. Offered
        // again on the next connect.
        var stale = CurrentGeneration() != gen0;
        if (stale || install && _latestBusy && !resumes) {
            _surface.Status(stale ? PromptStaleStatus : PromptBusyStatus);
            lock (_lock) _windowsServiceOffered = false;
            return;
        }
        if (install) await MoveToWindowsServiceAsync(ct).ConfigureAwait(false);
        else await RunLaneMutationAsync(MutationVerb.InstallWindowsService, ct).ConfigureAwait(false);
    }

    /// The takeover stops the task's daemon before the service starts its own, so for that gap the banner
    /// says why there is no daemon instead of reporting it down.
    async Task<MutationOutcome?> MoveToWindowsServiceAsync(CancellationToken ct) {
        lock (_lock) _movingToWindowsService = true;
        _isMovingToService.OnNext(true);
        _surface.Status(MovingToWindowsServiceStatus);
        var outcome = await RunLaneMutationOutcomeAsync(MutationVerb.InstallWindowsService, ct).ConfigureAwait(false);
        if (outcome is not (MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout)) WithdrawMovingToWindowsService();
        return outcome;
    }

    void WithdrawMovingToWindowsService() {
        lock (_lock) {
            if (!_movingToWindowsService) return;
            _movingToWindowsService = false;
        }
        _isMovingToService.OnNext(false);
        _surface.Withdraw(MovingToWindowsServiceStatus);
    }

    static bool OffersWindowsService(ServiceSnapshot snap) =>
        (snap.Supervisor == "windows_scheduled_task" && snap.WindowsServiceInstallable == true)
        || (snap.Supervisor == "windows_service" && snap.ServiceUpgradeAvailable == true);

    /// <summary>The explicit "run as a Windows service" action in Settings, available after a declined offer</summary>
    /// <returns>Null when nothing ran: the app is shutting down or must restart first</returns>
    public async Task<MutationOutcome?> InstallWindowsServiceAsync(CancellationToken ct) {
        try {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            if (!await TryAcquireGateAsync(linked.Token).ConfigureAwait(false)) return null;
            try {
                return await MoveToWindowsServiceAsync(linked.Token).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            return null;
        }
    }

    async Task OfferRepairAsync(ServiceSnapshot snap, CancellationToken ct, bool requireIdle = false) {
        var missing = await FailingRepairPreconditionAsync(snap).ConfigureAwait(false);
        if (missing is not null) {
            _surface.Status(missing);
            return;
        }

        var terminalPath = await _probe.TerminalPathAsync(ct).ConfigureAwait(false);
        var prompt = new LifecyclePrompt(LifecyclePrompt.KindRepair, null, null, terminalPath is null, TakeoverDisclosure);

        await ConfirmAndReplaceAsync(prompt, ct, requireIdle).ConfigureAwait(false);
    }

    /// App shutdown awaits this: completes once no mutation child is in flight. Does not itself
    /// block new mutations from starting — callers stop feeding triggers (DisposeAsync
    /// unsubscribes first) before relying on this as a barrier.
    public async Task QuiescedAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;

        _subscription?.Dispose();
        _snapshots?.Dispose();
        _lifetime.Cancel();
        await QuiescedAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
