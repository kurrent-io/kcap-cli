using System.Collections.Frozen;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Remote.Models;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One entry of the repository chip's menu. Vendor is the remembered harness for RepoPath, or
/// HomeViewModel.DefaultVendor when none was ever chosen there; Selected marks the entry that
/// matches SelectedRepoPath under HomeViewModel's own path comparison.
public sealed record RepositoryOption(string RepoPath, string Vendor, bool Selected);

/// One entry of the launcher's machine chip: the local daemon, or one of the viewer's own remote
/// daemons — name-based launch routing is only defined within one owner, so a daemon owned by
/// someone else is never surfaced as an option. RepoPaths/SupportedVendors come from DaemonInfo
/// verbatim for a remote machine; the local entry's come from the existing repo flow. Version is
/// the remote daemon's own advertised build, which is what the attachment gate reads; null for the
/// local entry, whose capability comes from the attach handshake instead. VendorModels is the
/// machine's own model catalog per vendor; null when its daemon advertises none. VendorModelsByAccount
/// is the catalog per registered account id.
public sealed record MachineOption(
    string DaemonName, bool IsLocal, bool Connected, string? Platform,
    string[] RepoPaths, string[]? SupportedVendors, bool Selected, string? Version = null,
    IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? VendorModels = null,
    IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? VendorModelsByAccount = null);

/// Whether a launch can reach a daemon right now, merged from the local attach state and the
/// daemon's own upstream connection word — the same two inputs the footer's status line reads.
internal enum LaunchAvailability { Ready, Pending, DaemonUnavailable, ServerDisconnected }

/// The launcher pane's view-model: repository + harness picker, a free-text goal, and
/// the Start action that launches a session through ILaunchClient. Constructed once, like
/// TrayViewModel/ActivityViewModel — not gated behind IActivatableViewModel — since Harnesses
/// must be live from construction, not deferred to a window's activation. Snapshots are mutated
/// on the daemon client's own background thread (same as MainWindowViewModel/
/// ConsentPromptViewModel), so projections ObserveOn RxSchedulers.MainThreadScheduler BEFORE the
/// operator that touches bound state — a binding must never see a mutation off the UI thread.
public sealed class HomeViewModel : ReactiveObject, IDisposable, IAttachmentSink {
    /// A repository with no remembered choice falls back to this — never to whatever vendor was
    /// selected for a DIFFERENT repository, which would leak a preference across repositories.
    public const string DefaultVendor = "claude";

    public const string DefaultPermissionMode = ClaudePermissionModes.Manual;

    /// Reserved key for the not-yet-in-a-repository target. It is a normal AppState.HarnessByRepo
    /// key, so it round-trips and keeps its own remembered harness like any real repo path —
    /// but LAUNCHING it is not supported yet: AgentOrchestrator rejects a launch whose repo path
    /// fails Directory.Exists, which "" does. Until the daemon accepts a repo-less launch this is
    /// a storage key only.
    public const string ScratchRepoPath = "";

    /// A launch that started but handed back an id nothing can open. The session is real — open
    /// it from the session list rather than treating this as a launch failure.
    public const string UnusableIdMessage = "Launched, but the session ID was unusable. Open it from the session list.";

    /// StartAsync's own re-check refuses this even if a caller invoked ReactiveCommand.Execute()
    /// straight past CanExecute — the wire request is never built for a machine that failed the
    /// ownership/connected check at the moment of launch, whatever the UI-affordance state said.
    public const string MachineUnavailableMessage = "This machine is no longer available. Choose a different one.";

    internal const string SignInToAttach = "sign in to attach files";

    internal const string ConnectingNotice     = "Connecting to the server. Session activity appears when the connection is ready.";
    /// Longer than the daemon's 30s connect backoff so one redial still reads as connecting.
    internal static readonly TimeSpan CatchUpLimit = TimeSpan.FromSeconds(60);
    internal const string FinishingSignInNotice =
        "Finishing sign-in. Reconnecting to the server…";
    internal const string DaemonDownNotice     =
        "The daemon isn't running. Agents can't start until it's back.";
    internal const string DaemonRestartingNotice =
        "The daemon is restarting to update. It reconnects on its own.";
    internal const string DaemonIncompatibleNotice =
        "The app and daemon versions don't match. Update both, then reconnect.";
    internal const string ServerLostNotice     = "Not connected to the server. Sign in again to reconnect.";
    internal const string SignInExpiredNotice  = "Your sign-in has expired. Sign in again to reconnect.";
    /// A daemon attached by name that serves another server: signing in again cannot move it.
    internal static string DaemonOnOtherServerNotice(string daemonServer, string appServer) {
        var (daemonLabel, appLabel) = AttachedIdentity.ServerLabels(daemonServer, appServer);
        return $"This machine's daemon uses another server. It's connected to {daemonLabel}, so it can't run agents for {appLabel}.";
    }

    /// The daemon's server when it is known and is not the app's; null otherwise, an unknown side included.
    internal static string? ForeignDaemonServer(string? daemonServer, string? appServer) =>
        string.IsNullOrEmpty(daemonServer) || string.IsNullOrEmpty(appServer) || ServerIdentity.SameServer(daemonServer, appServer)
            ? null : daemonServer;

    internal const string DaemonSignedOutNotice = "This machine's daemon is signed out. Sign in to its profile to reconnect.";

    /// The connection word the notice reads when the daemon reports its own sign-in as the reason its
    /// hub is down; never on the wire.
    internal const string DaemonSignedOutWord = "signed out";
    internal const string LocalServerRefusedNotice = "The local server refused the connection. Restart kcap.";
    internal const string FinishingUpdateNotice    = "Finishing the update…";
    internal const string LocalServerLostNotice    = "Lost the connection to the local server. kcap keeps retrying.";

    /// A service-run daemon restarts itself for a harness installed on its PATH; setup fixes a PATH that
    /// misses it, and a daemon started by hand needs a restart.
    internal static string NoHarnessesNotice(string? remoteMachine) => remoteMachine is null
        ? "No coding agents found on this machine. Install Claude Code, Codex or another supported agent. If it doesn't show up, run setup again or restart the daemon."
        : $"No coding agents found on {remoteMachine}. Install one there. If it doesn't show up, run setup there again or restart its daemon.";

    internal static string HarnessUnavailableMessage(string label) => $"{label} isn't available on this machine. Choose another agent.";

    /// The title over a daemon status that arrives alone, so the card keeps a title and a body.
    internal const string DaemonStatusTitle = "Daemon status";

    internal static string RemoteOfflineNotice(string machine) => $"{machine} is offline. Launches resume when it reconnects.";

    const string IncompatibleReason = "daemon_incompatible";

    readonly IDaemonClientService _daemon;
    readonly IAppStateStore _state;
    readonly ILaunchClient _launch;
    readonly Func<Task<string[]>> _knownRepos;
    readonly Func<int>? _navigationGeneration;
    readonly Action<string, AgentOrigin, int>? _openSessionIfCurrent;
    readonly Action<string>? _launchFailed;
    readonly CompositeDisposable _disposables = new();

    string _selectedRepoPath = ScratchRepoPath;
    // Subject (not WhenAnyValue) so the ctor can compose StartButtonTip — same reason as
    // _signInRequired below.
    readonly BehaviorSubject<string> _selectedRepoPathChanges = new(ScratchRepoPath);
    public string SelectedRepoPath {
        get => _selectedRepoPath;
        set {
            // Same rule as SameLaunchTarget and the picker, so a refusal clears exactly when they see a new folder.
            var changed = !PathComparerFor(RemoteMachineSelected).Equals(_selectedRepoPath, value);
            this.RaiseAndSetIfChanged(ref _selectedRepoPath, value);
            _selectedRepoPathChanges.OnNext(value);
            // A launch refusal describes the folder that failed. A later pick is a different attempt.
            if (changed && StartError != _intakeNotice) StartError = null;
        }
    }

    string _selectedVendor = DefaultVendor;
    // Follows the repository — only ChooseHarnessAsync/SelectRepositoryAsync set it, so a caller
    // can never desync it from the persisted-or-default rule those two methods implement.
    public string SelectedVendor {
        get => _selectedVendor;
        private set => this.RaiseAndSetIfChanged(ref _selectedVendor, value);
    }

    string _selectedModel = "";
    /// "" = vendor default (the wire convention). Session-scoped, not persisted; reset whenever
    /// the vendor changes — model ids are vendor-specific, so a stale one would misfire.
    public string SelectedModel {
        get => _selectedModel;
        set => this.RaiseAndSetIfChanged(ref _selectedModel, value);
    }

    /// The selected machine's own model catalog merged over the server's (see MergeCatalogs), with
    /// the selected account's own list for its vendor where the machine has one; the agent chip
    /// resolves a selected model's label against it.
    public IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> EffectiveModelCatalog => _effectiveModelCatalog.Value;

    /// The model choices to offer for a vendor: the effective catalog's entry, else the curated
    /// fallback. So a launch is never blocked by an empty or unreachable server catalog.
    public IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor) =>
        EffectiveModelCatalog.TryGetValue(vendor, out var models) ? models : HostedHarnessCatalog.ModelChoicesFor(vendor);

    /// Per vendor: the machine's list whenever it has the key, even empty, since the machine is
    /// the one that launches; else a non-empty server list; else no key.
    internal static bool ShouldClearSelection(string vendor, string model,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> previous,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> next) {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (!previous.TryGetValue(vendor, out var was) || !Lists(was, model)) return false;
        return next.TryGetValue(vendor, out var now) && !Lists(now, model);

        static bool Lists(IReadOnlyList<ModelChoice> models, string model) =>
            models.Any(m => string.Equals(m.Slug, model, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> MergeCatalogs(
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? machine,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> server) {
        var merged = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vendor, models) in server)
            if (models.Count > 0) merged[vendor] = models;
        if (machine is not null)
            foreach (var (vendor, models) in machine) merged[vendor] = models;
        return merged;
    }

    string? _selectedEffort;
    /// null = vendor default. Survives vendor changes — the effort vocabulary is shared enough
    /// (low/medium/high/xhigh/max) that the choice usually still means what the user meant.
    public string? SelectedEffort {
        get => _selectedEffort;
        set => this.RaiseAndSetIfChanged(ref _selectedEffort, value);
    }

    string _selectedPermissionMode = DefaultPermissionMode;
    /// A ClaudePermissionModes token. Session-scoped like the effort and kept across vendor
    /// changes; PermissionModeFor decides whether it rides a given launch.
    public string SelectedPermissionMode {
        get => _selectedPermissionMode;
        set => this.RaiseAndSetIfChanged(ref _selectedPermissionMode, value);
    }

    string _selectedCodexSandbox = HostedHarnessCatalog.DefaultCodexSandbox;
    /// Kept across vendor changes; CodexPostureFor decides whether it rides a given launch.
    public string SelectedCodexSandbox {
        get => _selectedCodexSandbox;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexSandbox, value);
    }

    string _selectedCodexApproval = HostedHarnessCatalog.DefaultCodexApproval;
    public string SelectedCodexApproval {
        get => _selectedCodexApproval;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexApproval, value);
    }

    string _goal = "";
    int _goalEdits;
    /// Every real edit bumps _goalEdits: a launch clears only the goal it captured, and only while
    /// nothing has been typed into it since.
    public string Goal {
        get => _goal;
        set {
            if (string.Equals(_goal, value, StringComparison.Ordinal)) return;
            _goalEdits++;
            ClearIntakeNotice();
            this.RaiseAndSetIfChanged(ref _goal, value);
        }
    }

    /// The chips staged for the next launch. Cleared only of what a launch actually sent.
    public AttachmentTray Tray { get; } = new();

    /// Where every intake source (paste, drop, picker) hands its result.
    public IAttachmentSink Attachments => this;

    readonly BehaviorSubject<bool> _uploadingChanges = new(false);
    bool _uploading;
    /// True while a launch's staged files are going up. Start is blocked, never queued.
    public bool Uploading {
        get => _uploading;
        private set {
            if (_uploading == value) return;
            this.RaiseAndSetIfChanged(ref _uploading, value);
            // A window closing mid-upload disposes this object while StartAsync is still inside its
            // try, and the finally that clears the flag would then publish onto a disposed subject.
            if (!_disposed) _uploadingChanges.OnNext(value);
        }
    }

    readonly ObservableAsPropertyHelper<bool> _canAttach;
    /// Whether the selected machine can take files right now: signed in, and that machine's build
    /// accepts attachment ids.
    public bool CanAttach => _canAttach.Value;

    readonly ObservableAsPropertyHelper<string?> _attachHint;
    /// Why CanAttach is false, or null when it isn't.
    public string? AttachHint => _attachHint.Value;
    public int FreeSlots => Tray.FreeSlots;

    /// The refusal line from the last intake. Held apart from StartError so the next edit or
    /// intake can retract exactly it, and nothing else that has since been reported.
    string? _intakeNotice;

    string? _startError;
    /// Any write withdraws Use default; only an account refusal offers it again.
    public string? StartError {
        get => _startError;
        private set {
            this.RaiseAndSetIfChanged(ref _startError, value);
            StartErrorOffersDefault = false;
        }
    }

    /// A 401 outcome is the only writer besides its own reset paths. A subject (not a reactive
    /// property read via WhenAnyValue) so the ctor can compose it — see SessionRailViewModel's
    /// _selectedSessionKeyChanges for why WhenAnyValue is avoided in constructors here.
    readonly BehaviorSubject<bool> _signInRequired = new(false);
    /// True after a successful re-auth until the daemon reports server-connected (or goes down).
    /// Keeps the banner from still asking to Sign in while the daemon catches up.
    readonly BehaviorSubject<bool> _awaitingServerAfterSignIn = new(false);
    /// True once a Connecting/Finishing notice has lasted CatchUpLimit — Sign in is then the remaining affordance.
    readonly BehaviorSubject<bool> _catchUpTimedOut = new(false);
    // A local server takes no sign-in: its recovery is the Start banner.
    readonly BehaviorSubject<bool> _localServer = new(false);
    readonly BehaviorSubject<bool> _localServerDown = new(false);
    readonly BehaviorSubject<bool> _updateFinishing = new(false);
    string? _updateDetail;
    readonly Action? _requestSignIn;

    readonly ObservableAsPropertyHelper<string?> _connectionNotice;
    /// Why launching is unavailable right now, or null when it isn't. The sign-in-expired text
    /// wins over the connection-derived one — it is the more specific diagnosis.
    public string? ConnectionNotice => _connectionNotice.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerMessage;
    /// The connection explanation, or the local daemon's status when there is none.
    public string? BannerMessage => _bannerMessage.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerTitle;
    public string? BannerTitle => _bannerTitle.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerBody;
    public string? BannerBody => _bannerBody.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerDetail;
    /// Local daemon status and system output that goes with the message, without its commands.
    public string? BannerDetail => _bannerDetail.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerCommand;
    /// A command the banner asks the user to run, lifted out of the prose so it can be copied.
    public string? BannerCommand => _bannerCommand.Value;

    readonly ObservableAsPropertyHelper<bool> _connectionBannerVisible;
    /// Same connection/sign-in/daemon banner the launcher shows above the composer.
    public bool ConnectionBannerVisible => _connectionBannerVisible.Value;

    /// The usage-data disclosure, shown on the launcher until it is acknowledged.
    public UsageDataNotice? UsageNotice { get; init; }

    readonly ObservableAsPropertyHelper<bool> _bannerBusy;
    /// True while the banner is an in-flight connect/finish line — a loader, not Sign in.
    public bool BannerBusy => _bannerBusy.Value;

    readonly ObservableAsPropertyHelper<bool> _bannerFailed;
    /// True when the banner reports a failure rather than something that needs the user.
    public bool BannerFailed => _bannerFailed.Value;

    readonly ObservableAsPropertyHelper<bool> _signInVisible;
    public bool SignInVisible => _signInVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _bannerActionsVisible;
    public bool BannerActionsVisible => _bannerActionsVisible.Value;

    /// Lane SignedOut or a 401 launch — the same flag NoticeFor uses for SignInExpiredNotice.
    /// An observable (not WhenAnyValue) so MainWindow can fold it into the rail footer without
    /// ReactiveUI's property-change mixins, which a headless test run does not reliably prime.
    public IObservable<bool> SignInExpired { get; }

    ObservableAsPropertyHelper<bool>? _daemonStartVisible;
    public bool DaemonStartVisible => _daemonStartVisible?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _daemonRetryVisible;
    public bool DaemonRetryVisible => _daemonRetryVisible?.Value ?? false;

    // Feeds BannerMessage before AttachDaemonRecovery runs (null) and after (lifecycle lane).
    readonly BehaviorSubject<string?> _daemonStartMessageFeed = new(null);
    // Same lane for the action cluster: false until AttachDaemonRecovery subscribes the real predicates.
    readonly BehaviorSubject<bool> _daemonStartVisibleFeed = new(false);
    readonly BehaviorSubject<bool> _daemonRetryVisibleFeed = new(false);
    readonly BehaviorSubject<bool> _daemonRepairVisibleFeed = new(false);

    /// Wired by AttachDaemonRecovery to MainWindow's Start/Reconnect commands.
    public ReactiveCommand<Unit, Unit>? StartDaemonCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? RetryDaemonCommand { get; private set; }

    ObservableAsPropertyHelper<bool>? _daemonRepairVisible;
    /// Only while the banner shows the very line the repair answers, never beside another notice.
    public bool DaemonRepairVisible => _daemonRepairVisible?.Value ?? false;
    public ReactiveCommand<Unit, Unit>? RepairDaemonCommand { get; private set; }

    readonly ObservableAsPropertyHelper<string> _startButtonTip;
    /// Tip when Start is disabled (no repo / connection gate). ToolTip.ShowOnDisabled is required.
    public string StartButtonTip => _startButtonTip.Value;

    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    readonly AccountChooser _accountChooser;

    IReadOnlyDictionary<string, AccountUsageView> _accountUsageById = FrozenDictionary<string, AccountUsageView>.Empty;
    /// Keyed by registered account id, for the selected host's selected vendor; empty while the host's
    /// readings may not be drawn.
    public IReadOnlyDictionary<string, AccountUsageView> AccountUsageById {
        get => _accountUsageById;
        private set => this.RaiseAndSetIfChanged(ref _accountUsageById, value);
    }
    readonly Func<Task>? _retryListing;
    (string RepoPath, string Vendor, string Machine, bool Remote)? _accountTarget;
    bool _applyingAccountInputs;
    IReadOnlyDictionary<string, string>? _accountMemory;
    readonly TaskCompletionSource _accountMemoryLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// Completes once the remembered accounts have been read, which happens off the UI thread.
    internal Task AccountMemoryLoadedForTesting => _accountMemoryLoaded.Task;
    Task _accountMemoryWrites = Task.CompletedTask;
    // Live mirrors of the account inputs, read by StartAsync against its captured draft.
    DaemonListing _currentListing = new(null, 0, false);
    IReadOnlyList<DaemonAccountsDto> _currentDaemonAccounts = [];
    AccountsSnapshotDto? _currentLocalAccounts;
    readonly BehaviorSubject<bool> _accountPending = new(false);
    readonly BehaviorSubject<string?> _accountTip = new(null);
    // The account pipeline's copy of _lastViewerId. Unresolved reads as Unknown and is read again once
    // it resolves; resolved to null is a server without sign-in.
    readonly BehaviorSubject<(bool Resolved, string? Id)> _viewerIds = new((false, null));
    readonly BehaviorSubject<(string Vendor, string AccountId)?> _accountCatalogKey = new(null);

    bool _accountSection;
    /// The Agent panel's Account section: the rows, or the pending line while the choice resolves.
    public bool AccountSection {
        get => _accountSection;
        private set => this.RaiseAndSetIfChanged(ref _accountSection, value);
    }

    IReadOnlyList<AccountChoice> _accountRows = [];
    public IReadOnlyList<AccountChoice> AccountRows {
        get => _accountRows;
        private set => this.RaiseAndSetIfChanged(ref _accountRows, value);
    }

    string? _accountPendingText;
    public string? AccountPendingText {
        get => _accountPendingText;
        private set => this.RaiseAndSetIfChanged(ref _accountPendingText, value);
    }

    bool _accountRetryVisible;
    public bool AccountRetryVisible {
        get => _accountRetryVisible;
        private set => this.RaiseAndSetIfChanged(ref _accountRetryVisible, value);
    }

    string? _selectedAccountLabel;
    /// The chip's account part; null while the default is chosen.
    public string? SelectedAccountLabel {
        get => _selectedAccountLabel;
        private set => this.RaiseAndSetIfChanged(ref _selectedAccountLabel, value);
    }

    bool _startErrorOffersDefault;
    public bool StartErrorOffersDefault {
        get => _startErrorOffersDefault;
        private set => this.RaiseAndSetIfChanged(ref _startErrorOffersDefault, value);
    }

    public ReactiveCommand<Unit, Unit> RetryAccountsCommand { get; }
    public ReactiveCommand<Unit, Unit> UseDefaultAccountCommand { get; }

    readonly ObservableAsPropertyHelper<IReadOnlyList<HarnessOption>> _harnesses;
    public IReadOnlyList<HarnessOption> Harnesses => _harnesses.Value;

    readonly IObservable<IReadOnlyList<DaemonInfo>> _daemons;
    readonly Func<CancellationToken, Task<string?>> _viewerId;
    readonly IObservable<ServerLaneStatus> _laneStatus;
    readonly string? _localMachineId;

    /// Live mirror of the local availability CombineLatest below, read (never bound directly) by
    /// ListMachinesAsync to stamp the local MachineOption's Connected flag at the moment asked.
    LaunchAvailability _currentAvailability = LaunchAvailability.Pending;

    /// Live mirror of the local daemon's latest advertised SupportedVendors — read (never bound
    /// directly) when a machine selection reverts to local, to revalidate SelectedVendor against
    /// what the local daemon actually hosts rather than leaving whatever a prior remote pick set.
    string[]? _currentLocalVendors;

    /// Set by OwnDaemonsAsync every time it resolves viewerId — the CanExecute pipeline
    /// (FindMachine) reads it to re-verify ownership synchronously, since it cannot itself await
    /// viewerId on every lane/daemons emission. Null until first resolved, or whenever viewerId
    /// itself resolves to null.
    string? _lastViewerId;

    string _selectedMachine;
    /// Which daemon a launch targets: the local one until SelectMachineAsync picks a remote name.
    public string SelectedMachine {
        get => _selectedMachine;
        private set => this.RaiseAndSetIfChanged(ref _selectedMachine, value);
    }

    bool _remoteMachineSelected;
    /// False ⇒ the local repo/harness/launch behavior, which is all a HomeViewModel that never
    /// wires the machine picker ever sees.
    public bool RemoteMachineSelected {
        get => _remoteMachineSelected;
        private set => this.RaiseAndSetIfChanged(ref _remoteMachineSelected, value);
    }

    // Drives the reactive pipelines below (CanExecute, Harnesses) — a subject rather than
    // WhenAnyValue, same reason as _selectedRepoPathChanges.
    readonly BehaviorSubject<(string Name, bool Remote)> _machineSelectionChanges;
    // null = local (daemon.Snapshots governs Harnesses); non-null = the remote machine's own
    // advertised vendors, set by SelectMachineAsync.
    readonly BehaviorSubject<string[]?> _remoteVendorOverride = new(null);

    readonly ObservableAsPropertyHelper<bool> _machinePickerVisible;
    /// The machine chip's visibility: hidden until the viewer owns at least one remote daemon.
    public bool MachinePickerVisible => _machinePickerVisible.Value;

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    /// A launch must be cancellable: the app disposes the launch client (and its HubConnection) on
    /// shutdown, so an in-flight hub invoke holding no token races that teardown.
    readonly CancellationToken _shutdown;
    volatile bool _disposed;

    // The id RequestLaunchAgentV2 hands back is request-accepted, not success: failure arrives
    // later as a LaunchFailed broadcast, success as the agent's row appearing. StartAsync tracks
    // the id here until one of those settles it; one lock covers both maps and the retained draft,
    // since StartAsync, the failure subscription and the rows subscription all touch them.
    readonly object _launchTrackingLock = new();
    // Keyed by the normalized agent id; the lane says which directory rows can settle the launch,
    // since a same-id row on the other lane is a different agent.
    readonly Dictionary<string, PendingLaunch> _pendingLaunches = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Reason, DateTime At)> _recentFailures = new(StringComparer.Ordinal);
    static readonly TimeSpan PendingLaunchTtl = TimeSpan.FromMinutes(10);
    static readonly TimeSpan RecentFailureTtl = TimeSpan.FromSeconds(30);
    readonly IAgentDirectory? _directory;

    /// One accepted launch awaiting settlement. UploadedAt is null for a text-only launch; both
    /// stamps come from the same clock, so one TimeProvider governs every expiry. Generation is
    /// the navigation generation captured before the launch call.
    sealed record PendingLaunch(
        DateTimeOffset At, AgentOrigin Lane, bool HadAttachments, DateTimeOffset? UploadedAt,
        string RepoPath, int Generation, string Machine);

    /// The only copy of a sent launch's bytes, held until the daemon settles that launch or the
    /// window closes. The two counters are what the composer looked like AFTER the launch emptied
    /// it: restoring over anything the user has done since would overwrite their work.
    sealed record RetainedDraft(
        string AgentId, LaunchDraft Draft, DateTimeOffset UploadedAt,
        bool ClearedGoal, int GoalEditsAfterClear, bool ClearedTray, int TrayGenerationAfterClear);

    /// Only the latest accepted attachment launch keeps its bytes — one draft, replaced by the
    /// next, released on settlement or when the window closes.
    RetainedDraft? _retainedDraft;
    static readonly TimeSpan RetentionTtl = TimeSpan.FromMinutes(10);

    readonly IAttachmentUploader _uploader;
    readonly TimeProvider _time;
    readonly ITimer _retentionTimer;
    readonly ITimer _catchUpTimer;
    bool _catchUpArmed;
    long _catchUpGeneration;
    DateTimeOffset _catchUpDeadline;

    // Live mirrors of the attachment gate's inputs, read (never bound) by the in-method re-check
    // StartAsync runs against the captured draft rather than the current selection.
    bool _signedIn;
    IReadOnlyList<string>? _currentCapabilities;
    IReadOnlyList<DaemonInfo> _currentDaemons = [];

    readonly ObservableAsPropertyHelper<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> _effectiveModelCatalog;
    IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? _currentLocalCatalog;

    /// knownRepos is RepoPathStore.GetSortedPathsAsync in production — the same persisted list
    /// DaemonConnect.RepoPaths feeds the server's launch dialog. Required (no defaulted overload)
    /// so a test can never silently read the developer's own ~/.config/kcap/repos.json.
    /// <param name="navigationGeneration">
    /// Read BEFORE the launch call, never after: the captured value is what makes a success that
    /// lands after the user navigated away open nothing.
    /// </param>
    /// <param name="openSessionIfCurrent">
    /// The launch auto-open (MainWindowViewModel.OpenSessionIfCurrent), carrying the lane and that
    /// captured generation. Either lane's launch opens on its placeholder row at once.
    /// </param>
    /// <param name="requestSignIn">Opens the re-auth sign-in surface (App owns the window). Null
    /// leaves the Sign in button inert — a HomeViewModel with no windows to open.</param>
    /// <param name="daemons">The viewer's own-plus-others remote registry (IRemoteAgentsService.
    /// Daemons). Null ⇒ the machine picker offers only the local daemon.</param>
    /// <param name="viewerId">The signed-in user's own id (JwtClaims), read fresh per call. Null
    /// ⇒ no remote daemon is ever offered — ownership is never guessed.</param>
    /// <param name="laneStatus">The app's own server lane (IServerLane.Status) — what a remote
    /// launch's availability gates on, independent of the local daemon's own connection word.</param>
    /// <param name="localMachineId">This machine's id, for excluding the local daemon's own remote
    /// registry twin from the picker's remote options.</param>
    /// <param name="launchFailures">The app's own server lane (IServerLane.LaunchFailures). A
    /// failure whose id is being tracked renders as StartError; an unknown id is another client's
    /// launch and is ignored. Null ⇒ launch failures are never correlated.</param>
    /// <param name="directory">The merged local+remote rows (IAgentDirectory.Rows). A row for a
    /// tracked id is success confirmation and stops tracking it. Null ⇒ only a LaunchFailed or the
    /// pending entry's own 10-minute timeout ever stops tracking.</param>
    /// <param name="listing">The server's daemon listing (IRemoteAgentsService.Listing), which says
    /// whether each daemon, this machine's included, honours an explicit account. Null ⇒ no account
    /// is offered or sent.</param>
    /// <param name="daemonAccounts">Every listed daemon's accounts (IRemoteAgentsService.DaemonAccounts).</param>
    /// <param name="localAccounts">This machine's daemon's own snapshot (IAccountsService.Snapshot).</param>
    /// <param name="retryListing">Fetches the listing and every daemon's accounts again
    /// (IRemoteAgentsService.RefreshDaemonsAsync).</param>
    /// <param name="readings">The drawable snapshots and clock behind <see cref="AccountUsageById"/>.
    /// Null ⇒ no usage is shown.</param>
    /// <param name="launchFailed">A tracked launch failed (MainWindowViewModel.CloseFailedLaunch):
    /// its auto-opened workspace would otherwise cover the StartError this view just set.</param>
    public HomeViewModel(
            IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch,
            Func<Task<string[]>> knownRepos, TimeProvider time, CancellationToken shutdown = default,
            Func<int>? navigationGeneration = null,
            Action<string, AgentOrigin, int>? openSessionIfCurrent = null, Action? requestSignIn = null,
            IObservable<IReadOnlyList<DaemonInfo>>? daemons = null,
            Func<CancellationToken, Task<string?>>? viewerId = null,
            IObservable<ServerLaneStatus>? laneStatus = null, string? localMachineId = null,
            IObservable<LaunchFailure>? launchFailures = null, IAgentDirectory? directory = null,
            IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>? modelCatalog = null,
            IAttachmentUploader? uploader = null, string? appServerUrl = null,
            IObservable<DaemonListing>? listing = null, IObservable<IReadOnlyList<DaemonAccountsDto>>? daemonAccounts = null,
            IObservable<AccountsSnapshotDto?>? localAccounts = null, Func<Task>? retryListing = null,
            AccountReadings? readings = null, Action<string>? launchFailed = null) {
        _launchFailed = launchFailed;
        _daemon = daemon;
        _state = state;
        _launch = launch;
        _knownRepos = knownRepos;
        _shutdown = shutdown;
        _navigationGeneration = navigationGeneration;
        _openSessionIfCurrent = openSessionIfCurrent;
        _requestSignIn = requestSignIn;
        _daemons = daemons ?? Observable.Return<IReadOnlyList<DaemonInfo>>([]);
        _viewerId = viewerId ?? (_ => Task.FromResult<string?>(null));
        _laneStatus = laneStatus ?? Observable.Return(new ServerLaneStatus(ServerLaneState.Dormant));
        _localMachineId = localMachineId;
        _directory = directory;
        _uploader = uploader ?? new NoAttachmentUploader();
        _time = time;
        _selectedMachine = daemon.DaemonName;
        _machineSelectionChanges = new((daemon.DaemonName, false));
        _retentionTimer = _time.CreateTimer(
            _ => ReleaseExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _catchUpTimer = _time.CreateTimer(
            _ => OnCatchUpElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Never starts empty: a null SupportedVendors means "daemon
        // capability unknown", not "hosts nothing" — Build(null) offers everything until the first
        // real snapshot narrows it. A remote selection follows its live registry entry, so a
        // restarted daemon's new set applies; the list captured at selection covers a registry
        // that no longer resolves it. ObserveOn BEFORE ToProperty: Snapshots is pushed from the
        // daemon client's own background thread.
        var harnessOptions = daemon.Snapshots
            .Select(s => s.Daemon.SupportedVendors)
            .StartWith((string[]?)null)
            .CombineLatest(_remoteVendorOverride, _daemons.StartWith((IReadOnlyList<DaemonInfo>) []), _machineSelectionChanges,
                (localVendors, remoteVendors, list, sel) => HostedHarnessCatalog.Build(!sel.Remote ? localVendors
                    : FindMachine(list, sel.Name, _lastViewerId) is { } live ? live.SupportedVendors : remoteVendors));
        var noHarnesses = harnessOptions.Select(options => !options.Any(o => o.Available)).DistinctUntilChanged();
        _harnesses = harnessOptions
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Harnesses, HostedHarnessCatalog.Build(null))
            .DisposeWith(_disposables);
        // A daemon that restarts with a different set can leave the chip on a vendor it no longer hosts.
        harnessOptions
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(RevalidateVendor)
            .DisposeWith(_disposables);

        // The machine catalog follows the selection: the local snapshot's, or the selected remote
        // daemon's in the latest registry emission, resolved by name AND owner. A remote machine the
        // registry no longer resolves is unknown (null), never its last list. Any change re-renders
        // the agent chip, and the next flyout open reads the new rows (RebuildRows runs per open).
        var localCatalog = daemon.Snapshots
            .Select(s => (Models: VendorModelMaps.FromStatus(s.Daemon.VendorModels),
                ByAccount: VendorModelMaps.FromStatus(s.Daemon.VendorModelsByAccount)))
            .StartWith((Models: null, ByAccount: null));
        localCatalog.Subscribe(c => _currentLocalCatalog = c.Models).DisposeWith(_disposables);
        var machineCatalog = localCatalog.CombineLatest(
            _daemons.StartWith((IReadOnlyList<DaemonInfo>) []), _machineSelectionChanges,
            (local, list, sel) => {
                if (!sel.Remote) return local;
                var remote = FindMachine(list, sel.Name, _lastViewerId);
                return (Models: remote?.VendorModels, ByAccount: remote?.VendorModelsByAccount);
            });
        _effectiveModelCatalog = machineCatalog
            .CombineLatest(modelCatalog ?? Observable.Return(ServerVendorModelCatalog.Empty), _accountCatalogKey.DistinctUntilChanged(),
                (machine, server, account) => WithAccountCatalog(MergeCatalogs(machine.Models, server), machine.ByAccount, account))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.EffectiveModelCatalog, ServerVendorModelCatalog.Empty)
            .DisposeWith(_disposables);

        // A model the previous catalog listed and the next one withdraws resets to the default. A
        // model the previous catalog never listed (a typed custom id, or a pick made before any
        // catalog arrived) survives, or the custom row would be unusable.
        this.WhenAnyValue(x => x.EffectiveModelCatalog)
            .Scan((Prev: ServerVendorModelCatalog.Empty, Next: ServerVendorModelCatalog.Empty), (acc, next) => (acc.Next, next))
            .Subscribe(pair => {
                if (ShouldClearSelection(SelectedVendor, SelectedModel, pair.Prev, pair.Next)) SelectedModel = "";
            })
            .DisposeWith(_disposables);

        // A throw from viewerId (e.g. a claims-file read fault) is a missed visibility recompute,
        // never a fault that kills this OAPH's subscription forever (RemoteAgentsService's
        // identical philosophy for its own daemons refresh).
        var ownDaemonsNonEmpty = Observable.Switch(_daemons
            .Select(list => Observable.FromAsync(() => OwnDaemonsAsync(list)).Catch(Observable.Return<IReadOnlyList<DaemonInfo>>([]))))
            .Select(own => own.Count > 0);
        // OR'd with the live selection so an active remote pick is never stranded behind a
        // registry blip that empties the owned list out from under it.
        _machinePickerVisible = ownDaemonsNonEmpty
            .CombineLatest(_machineSelectionChanges, (hasOwn, sel) => hasOwn || sel.Remote)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.MachinePickerVisible, initialValue: false)
            .DisposeWith(_disposables);

        // The account choice follows the target: support from the server's listing (this machine's
        // daemon through its own row in it, never the socket), that host's snapshot, and memory.
        _accountChooser = new AccountChooser(
            (hostId, vendor, repoPath) => AccountMemory.Lookup(_accountMemory, hostId, vendor, repoPath), RememberAccount);
        _accountChooser.Changed += PublishAccountChoice;
        _retryListing = retryListing;
        if (listing is not null) {
            var remoteAccounts = daemonAccounts ?? Observable.Return<IReadOnlyList<DaemonAccountsDto>>([]);
            var localSnapshots = localAccounts ?? Observable.Return<AccountsSnapshotDto?>(null);
            listing.Subscribe(l => _currentListing = l).DisposeWith(_disposables);
            remoteAccounts.Subscribe(a => _currentDaemonAccounts = a).DisposeWith(_disposables);
            localSnapshots.Subscribe(s => _currentLocalAccounts = s).DisposeWith(_disposables);
            _machineSelectionChanges.DistinctUntilChanged()
                .CombineLatest(
                    // Ordinal: a case-only change is a new repository on a Linux remote; ApplyAccountInputs
                    // decides per machine whether it is.
                    _selectedRepoPathChanges.DistinctUntilChanged(StringComparer.Ordinal), this.WhenAnyValue(x => x.SelectedVendor),
                    listing.StartWith(new DaemonListing(null, 0, false)),
                    remoteAccounts.StartWith((IReadOnlyList<DaemonAccountsDto>) []),
                    localSnapshots.StartWith((AccountsSnapshotDto?) null),
                    _viewerIds.DistinctUntilChanged(),
                    (sel, repoPath, vendor, answer, remote, local, viewer) => (sel, repoPath, vendor, answer, remote, local, viewer))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(i => ApplyAccountInputs(i.sel, i.repoPath, i.vendor, i.answer, i.remote, i.local, i.viewer))
                .DisposeWith(_disposables);
            Observable.FromAsync(() => _state.LoadAsync())
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(state => {
                    _accountMemory = state.AccountByRepo;
                    _accountChooser.SetMemoryLoaded(true);
                    _accountMemoryLoaded.TrySetResult();
                })
                .DisposeWith(_disposables);
        }

        if (readings is not null) {
            Observable.Switch(_machineSelectionChanges.DistinctUntilChanged()
                    .Select(sel => sel.Remote ? readings.Remote(sel.Name) : readings.Local))
                .CombineLatest(this.WhenAnyValue(x => x.SelectedVendor), readings.Clock, ProjectUsage)
                .DistinctUntilChanged(EqualityComparer<IReadOnlyDictionary<string, AccountUsageView>>.Create((a, b) => UsageEquals(a!, b!), d => d.Count))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(usage => AccountUsageById = usage)
                .DisposeWith(_disposables);
        }

        // The word is seeded with "" (no snapshot yet): AvailabilityFor only reads it once the
        // attach state is Connected, by which point DaemonClientService's snapshot-before-Connected
        // ordering guarantees a real value is there (MainWindowViewModel's identical seam comment).
        var foreignServer = daemon.Snapshots.Select(s => ForeignDaemonServer(s.Daemon.ServerUrl, appServerUrl))
            .StartWith((string?)null).DistinctUntilChanged();
        // A daemon serving another server is connected to the wrong one: it cannot launch for this app.
        var daemonAvailability = daemon.Status.CombineLatest(
            daemon.Snapshots.Select(s => s.Daemon.Connection).StartWith(""), AvailabilityFor);
        var availability = daemonAvailability.CombineLatest(foreignServer,
            (local, foreign) => local is LaunchAvailability.Ready && foreign is not null ? LaunchAvailability.ServerDisconnected : local);
        availability.Subscribe(a => _currentAvailability = a).DisposeWith(_disposables);
        daemon.Snapshots.Select(s => s.Daemon.SupportedVendors)
            .Subscribe(v => _currentLocalVendors = v)
            .DisposeWith(_disposables);

        // A remote selection swaps in RemoteAvailabilityFor (lane + the selected daemon's latest
        // Connected AND still-owned-by-the-viewer) rather than changing AvailabilityFor itself, so
        // the local gate is the same for a caller that never picks a machine. FindMachine re-verifies ownership against _lastViewerId on every emission, so
        // a registry update that reassigns an already-selected name to a different owner revokes
        // launch readiness rather than trusting the selection made when it was still valid.
        // Shared with notices/signInState/StartButtonTip below — every surface that asks "can I
        // launch right now" reads the SAME selection-aware value, so a remote pick's banner can
        // never disagree with whether Start is actually enabled.
        var selectedAvailability = availability.CombineLatest(
            _laneStatus, _daemons, _machineSelectionChanges,
            (local, lane, list, sel) => sel.Remote ? RemoteAvailabilityFor(lane, FindMachine(list, sel.Name, _lastViewerId)) : local);

        // OR'd into _signInRequired at the read side (never written into the subject itself) so
        // NotifySignInCompleted's reset stays a clean false — a subject write here would let the
        // lane's still-SignedOut status immediately re-flip it back to true.
        var laneSignedOut = _laneStatus
            .Select(s => s.State == ServerLaneState.SignedOut)
            .DistinctUntilChanged();
        var signInRequired = _signInRequired.CombineLatest(laneSignedOut, (expired, lane) => expired || lane);
        SignInExpired = signInRequired;

        // IPC attaches by daemon name, so an existing local daemon can target another server.
        var selectedServerLane = _laneStatus.CombineLatest(
            daemon.Snapshots.Select(s => ServerIdentity.SameServer(s.Daemon.ServerUrl, appServerUrl)).StartWith(false),
            _machineSelectionChanges,
            (lane, sameServer, sel) => (
                State: sel.Remote || sameServer ? lane.State : ServerLaneState.Dormant,
                Applies: sel.Remote || sameServer));

        // Uploading an attachment needs a live server credential, so the gate reads the app's own
        // lane rather than the daemon's — and the machine half is asked of the machine that would
        // receive the files: the attach handshake locally, the daemon's advertised build remotely.
        var signedIn = _laneStatus
            .Select(s => s.State == ServerLaneState.Connected)
            .CombineLatest(signInRequired, (connected, expired) => connected && !expired)
            .DistinctUntilChanged();
        var localCapabilities = daemon.Status.Select(s => s.Capabilities);
        signedIn.Subscribe(v => _signedIn = v).DisposeWith(_disposables);
        localCapabilities.Subscribe(c => _currentCapabilities = c).DisposeWith(_disposables);
        _daemons.Subscribe(list => _currentDaemons = list).DisposeWith(_disposables);

        var canAttach = signedIn
            .CombineLatest(localCapabilities, _daemons, _machineSelectionChanges,
                (signed, caps, list, sel) => CanAttachTo(signed, caps, list, _lastViewerId, sel.Name, sel.Remote))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        _canAttach = canAttach
            .ToProperty(this, x => x.CanAttach, initialValue: false)
            .DisposeWith(_disposables);
        // ObserveOn BEFORE ToProperty, like every other bound projection here: signedIn is fed by
        // the server lane, which emits on its own connection thread.
        _attachHint = signedIn
            .CombineLatest(canAttach, AttachHintFor)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.AttachHint, initialValue: (string?)null)
            .DisposeWith(_disposables);

        var stagedCount = Observable.FromEvent<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => (_, e) => handler(e),
                h => Tray.PropertyChanged += h,
                h => Tray.PropertyChanged -= h)
            .Select(_ => Tray.Count)
            .StartWith(0);
        // Staged chips the target cannot take block Start rather than launching without them, and
        // an upload in flight owns the composer until it settles.
        // An update still bringing this machine's local server and daemon back holds local launches until it settles.
        var updateHolds = _updateFinishing.CombineLatest(_machineSelectionChanges, (finishing, sel) => finishing && !sel.Remote)
            .DistinctUntilChanged();
        var canLaunch = selectedAvailability.Select(a => a == LaunchAvailability.Ready)
            .CombineLatest(
                stagedCount.CombineLatest(canAttach, (count, attach) => count == 0 || attach),
                _uploadingChanges,
                noHarnesses,
                _accountPending.DistinctUntilChanged(),
                updateHolds,
                (ready, attachable, uploading, none, pending, held) => ready && attachable && !uploading && !none && !pending && !held);

        // Explicit ObserveOn: ReactiveCommand does NOT reschedule the supplied canExecute, and a
        // Status event arrives on the daemon client's pump thread (MainWindowViewModel's canStart
        // comment) — without it CanExecuteChanged would touch the bound Button off the UI thread.
        StartCommand = ReactiveCommand.CreateFromTask(
            StartAsync,
            canLaunch.ObserveOn(RxSchedulers.MainThreadScheduler));

        var signInState = selectedAvailability
            .CombineLatest(
                signInRequired,
                _awaitingServerAfterSignIn,
                selectedServerLane,
                foreignServer.CombineLatest(_machineSelectionChanges, (foreign, sel) => foreign is not null && !sel.Remote),
                (a, expired, awaiting, lane, foreign) => (Availability: a, Expired: expired, Awaiting: awaiting && lane.Applies, Lane: lane.State, Foreign: foreign))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        // Chained rather than one wide CombineLatest: local status/connection/signIn/awaiting
        // first, then folded against the selection-aware availability and the selection itself —
        // NoticeFor needs both to know whether a local-only notice (daemon down/incompatible)
        // may apply at all.
        var localNoticeInputs = daemon.Status
            .CombineLatest(
                daemon.Snapshots.Select(s => s.Daemon is { Credential: not null, Connection: not "connected" }
                    ? DaemonSignedOutWord
                    : s.Daemon.Connection).StartWith(""),
                signInRequired,
                _awaitingServerAfterSignIn,
                selectedServerLane,
                foreignServer,
                (status, connection, expired, awaiting, lane, foreign) => (status, connection, expired, awaiting: awaiting && lane.Applies, lane: lane.State, foreign));
        var rawNotices = localNoticeInputs
            .CombineLatest(selectedAvailability, _machineSelectionChanges,
                (n, avail, sel) => NoticeFor(n.status, n.connection, n.expired, n.awaiting, sel.Remote, avail, n.lane,
                    n.foreign is { } foreign ? DaemonOnOtherServerNotice(foreign, appServerUrl!) : null))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        rawNotices.Subscribe(ArmOrResetCatchUp).DisposeWith(_disposables);
        // A stopped local server's Start banner is the whole story, so the no-harness fallback stays
        // out of it whether or not the daemon has noticed the server is gone.
        var connectionNotices = rawNotices.CombineLatest(_catchUpTimedOut, NoticeAfterCatchUp)
            .CombineLatest(_localServer, _localServerDown, _machineSelectionChanges,
                (notice, local, down, sel) => NoticeForLocalServer(notice, local, down, sel.Remote ? sel.Name : null));
        var harnessNotice = noHarnesses.ObserveOn(RxSchedulers.MainThreadScheduler)
            .CombineLatest(_localServer, _localServerDown, _machineSelectionChanges,
                (none, local, down, sel) => none && !(local && down && !sel.Remote)
                    ? NoHarnessesNotice(sel.Remote ? sel.Name : null) : null);
        var notices = connectionNotices.CombineLatest(harnessNotice, (connection, harness) => connection ?? harness);
        _connectionNotice = notices
            .ToProperty(this, x => x.ConnectionNotice, ConnectingNotice)
            .DisposeWith(_disposables);
        // The lifecycle feed speaks for the LOCAL daemon (start attempts, version mismatch), so it
        // is shown only while the local machine is selected. Remote notices must stay independent.
        var bannerContent = connectionNotices.CombineLatest(
            _daemonStartMessageFeed, _machineSelectionChanges, harnessNotice,
            (notice, startMessage, sel, harness) => {
                var content = BannerContentFor(notice, sel.Remote ? null : startMessage);
                return content.Message is null ? (Message: harness, Detail: (string?)null) : content;
            });
        _bannerMessage = bannerContent.Select(content => content.Message)
            .ToProperty(this, x => x.BannerMessage, ConnectingNotice)
            .DisposeWith(_disposables);
        var bannerCard = bannerContent.Select(content => {
            var message = NoticeText.Commands(content.Message);
            var detail  = NoticeText.Commands(content.Detail);
            var (title, body) = NoticeText.Headline(message.Prose);
            if (body is null && detail.Prose is null && title is not null) (title, body) = (DaemonStatusTitle, message.Prose);
            if (BannerInFlight(content)) {
                title = title?.TrimEnd('…') + "…";
                body = body?.Replace("…", "", StringComparison.Ordinal);
                detail.Prose = detail.Prose?.Replace("…", "", StringComparison.Ordinal);
            }
            return (Title: title, Body: body, Detail: detail.Prose, Command: detail.Command ?? message.Command);
        });
        _bannerTitle = bannerCard.Select(card => card.Title)
            .ToProperty(this, x => x.BannerTitle, NoticeText.Headline(ConnectingNotice).Title)
            .DisposeWith(_disposables);
        _bannerBody = bannerCard.Select(card => card.Body)
            .ToProperty(this, x => x.BannerBody, NoticeText.Headline(ConnectingNotice).Body)
            .DisposeWith(_disposables);
        _bannerDetail = bannerCard.Select(card => card.Detail)
            .ToProperty(this, x => x.BannerDetail)
            .DisposeWith(_disposables);
        _bannerCommand = bannerCard.Select(card => card.Command)
            .ToProperty(this, x => x.BannerCommand)
            .DisposeWith(_disposables);
        Notices.TrackConnection(bannerContent.Select(content =>
            (content.Message is not null, BannerInFlight(content) ? LauncherNotices.Kind.Progress : LauncherNotices.Kind.Problem)));
        _connectionBannerVisible = bannerContent
            .Select(content => content.Message is not null)
            .ToProperty(this, x => x.ConnectionBannerVisible, initialValue: false)
            .DisposeWith(_disposables);
        _bannerBusy = bannerContent
            .Select(BannerInFlight)
            .ToProperty(this, x => x.BannerBusy, initialValue: true)
            .DisposeWith(_disposables);
        _bannerFailed = bannerContent
            .Select(BannerFailedFor)
            .ToProperty(this, x => x.BannerFailed, initialValue: false)
            .DisposeWith(_disposables);
        var signInVisible = signInState
            .CombineLatest(_catchUpTimedOut, _localServer, (t, timedOut, local) => !local && !t.Awaiting && (t.Expired || (timedOut && !t.Foreign) || (
                t.Availability == LaunchAvailability.ServerDisconnected && !t.Foreign && !LaneIsCatchingUp(t.Lane))));
        _signInVisible = signInVisible
            .ToProperty(this, x => x.SignInVisible, initialValue: false)
            .DisposeWith(_disposables);
        _bannerActionsVisible = _daemonStartVisibleFeed
            .CombineLatest(_daemonRetryVisibleFeed, _daemonRepairVisibleFeed, signInVisible,
                (start, retry, repair, signIn) => start || retry || repair || signIn)
            .ToProperty(this, x => x.BannerActionsVisible, initialValue: false)
            .DisposeWith(_disposables);

        // The daemon catching up ends the wait whichever server it serves.
        daemonAvailability
            .Where(a => a is LaunchAvailability.Ready or LaunchAvailability.DaemonUnavailable)
            .Subscribe(_ => _awaitingServerAfterSignIn.OnNext(false))
            .DisposeWith(_disposables);

        // SignedOut is the only lane outcome that means auth really failed. Connected is not
        // "caught up" — the daemon's connection word still lags the token write.
        _laneStatus
            .Select(s => s.State == ServerLaneState.SignedOut)
            .DistinctUntilChanged()
            .Where(t => t)
            .Subscribe(_ => _awaitingServerAfterSignIn.OnNext(false))
            .DisposeWith(_disposables);

        _startButtonTip = _selectedRepoPathChanges
            .CombineLatest(notices, _accountTip, updateHolds,
                (repo, notice, account, held) => TipFor(repo, held ? FinishingUpdateNotice : notice, account))
            .ToProperty(this, x => x.StartButtonTip, TipFor(SelectedRepoPath, ConnectingNotice, null))
            .DisposeWith(_disposables);

        SignInCommand = ReactiveCommand.Create(() => { _requestSignIn?.Invoke(); });
        RetryAccountsCommand = ReactiveCommand.CreateFromTask(() => _retryListing?.Invoke() ?? Task.CompletedTask);
        UseDefaultAccountCommand = ReactiveCommand.Create(UseDefaultAccount);

        (launchFailures ?? Observable.Empty<LaunchFailure>())
            .Do(RecordRecentFailure)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyFailureIfPending)
            .DisposeWith(_disposables);

        (directory?.Rows.Connect() ?? Observable.Empty<IChangeSet<AgentRow, string>>())
            .Subscribe(ConfirmPendingRows)
            .DisposeWith(_disposables);

        // Adopt a recent known repo when the launcher starts empty — fire-and-forget; the picker
        // also calls EnsureDefaultRepositoryAsync before listing.
        _ = EnsureDefaultRepositoryAsync();
    }

    /// MainWindow owns Start/Reconnect (lifecycle startAction + shutdown token). The launcher banner
    /// reuses those commands and the start-message lane so chrome and pane never diverge.
    public void AttachDaemonRecovery(
            ReactiveCommand<Unit, Unit> startDaemon,
            ReactiveCommand<Unit, Unit> retry,
            IObservable<bool> startVisible,
            IObservable<bool> retryVisible,
            IObservable<string?> startMessage) {
        StartDaemonCommand = startDaemon;
        RetryDaemonCommand = retry;
        this.RaisePropertyChanged(nameof(StartDaemonCommand));
        this.RaisePropertyChanged(nameof(RetryDaemonCommand));

        // Both act on this machine's daemon, so a remote pick's banner must not offer them.
        var local = _machineSelectionChanges.Select(sel => !sel.Remote);
        var startShown = startVisible.CombineLatest(local, (visible, isLocal) => visible && isLocal).DistinctUntilChanged();
        var retryShown = retryVisible.CombineLatest(local, (visible, isLocal) => visible && isLocal).DistinctUntilChanged();
        _daemonStartVisible = startShown
            .ToProperty(this, x => x.DaemonStartVisible, initialValue: false)
            .DisposeWith(_disposables);
        _daemonRetryVisible = retryShown
            .ToProperty(this, x => x.DaemonRetryVisible, initialValue: false)
            .DisposeWith(_disposables);
        startShown.Subscribe(_daemonStartVisibleFeed).DisposeWith(_disposables);
        retryShown.Subscribe(_daemonRetryVisibleFeed).DisposeWith(_disposables);
        startMessage
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_daemonStartMessageFeed)
            .DisposeWith(_disposables);
    }

    /// The banner's Repair button, offered beside the lifecycle line <paramref name="offer"/> names.
    public void AttachServiceRepair(Func<CancellationToken, Task> repair, IObservable<string?> offer, CancellationToken ct) {
        RepairDaemonCommand = ReactiveCommand.CreateFromTask(() => repair(ct));
        this.RaisePropertyChanged(nameof(RepairDaemonCommand));

        var repairVisible = offer
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .CombineLatest(this.WhenAnyValue(x => x.BannerMessage, x => x.BannerDetail, (message, detail) => (message, detail)),
                (offered, shown) => offered is not null
                    && (shown.message == offered || NoticeText.Commands(offered).Prose == shown.detail));
        _daemonRepairVisible = repairVisible
            .ToProperty(this, x => x.DaemonRepairVisible, initialValue: false)
            .DisposeWith(_disposables);
        repairVisible.Subscribe(_daemonRepairVisibleFeed).DisposeWith(_disposables);
    }

    /// The "local server is not running" banner, beside the daemon's own recovery.
    public LocalServerRecoveryViewModel? LocalServer { get; private set; }

    /// The local stack's upgrade banner; null unless the server is this machine's local installation.
    public LocalStackViewModel? LocalStack {
        get => _localStack;
        init {
            _localStack = value;
            if (value is not null)
                Notices.TrackStack(value.WhenAnyValue(x => x.BannerVisible, x => x.Upgrading, x => x.IsNote, (visible, upgrading, note) =>
                    (visible, upgrading ? LauncherNotices.Kind.Progress : note ? LauncherNotices.Kind.Note : LauncherNotices.Kind.Problem)));
        }
    }
    readonly LocalStackViewModel? _localStack;

    /// Which of the launcher's problem notices show.
    public LauncherNotices Notices { get; } = new();

    /// True while an update the user went on from early is still finishing on this machine.
    public IObservable<bool> UpdateFinishing => _updateFinishing.DistinctUntilChanged();

    /// The update's remaining step, while one still runs after the main window opened early.
    public string? UpdateDetail {
        get => _updateDetail;
        private set => this.RaiseAndSetIfChanged(ref _updateDetail, value);
    }

    /// Follows an update the user went on from before it finished: with this machine selected, one card stands in
    /// for the others and launches wait, until every step has settled.
    public void AttachUpdateProgress(IObservable<(bool Finishing, string? Detail)> progress) {
        progress.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(p => {
            UpdateDetail = p.Detail?.TrimEnd('…');
            _updateFinishing.OnNext(p.Finishing);
        }).DisposeWith(_disposables);
        // A remote machine's own notices still show: the update is changing only this one.
        Notices.TrackUpdate(_updateFinishing.CombineLatest(_machineSelectionChanges, (finishing, sel) => finishing && !sel.Remote)
            .DistinctUntilChanged());
    }

    public void AttachLocalServerRecovery(LocalServerRecoveryViewModel recovery) {
        LocalServer = recovery;
        this.RaisePropertyChanged(nameof(LocalServer));
        _localServer.OnNext(recovery.IsLocalServer);
        recovery.WhenAnyValue(x => x.ServerDown).Subscribe(_localServerDown).DisposeWith(_disposables);
        Notices.TrackServer(recovery.WhenAnyValue(x => x.Visible, x => x.IsStarting, (visible, starting) =>
            (visible, starting ? LauncherNotices.Kind.Progress : LauncherNotices.Kind.Problem)));
    }

    /// The explanation first and daemon output below it. An expired sign-in stays the whole message:
    /// starting or reconnecting the daemon cannot restore that session. A status that hands the user a
    /// command to run is the whole message too, since it already says what the explanation would.
    internal static (string? Message, string? Detail) BannerContentFor(string? connectionNotice, string? startMessage) =>
        connectionNotice == SignInExpiredNotice || string.IsNullOrEmpty(startMessage) || startMessage == connectionNotice
            ? (connectionNotice, null)
        : connectionNotice is null || NoticeText.Commands(startMessage).Command is not null
            ? (startMessage, null)
            : (connectionNotice, startMessage);

    /// The re-auth dialog's success lands here (App wires it): clears the expired flag and holds a
    /// finishing notice until the daemon reports the server is connected.
    public void NotifySignInCompleted() {
        _signInRequired.OnNext(false);
        _awaitingServerAfterSignIn.OnNext(true);
        RestartCatchUp();
    }

    void ArmOrResetCatchUp(string? rawNotice) {
        if (!BusyNotice(rawNotice)) {
            StopCatchUp();
            return;
        }
        if (_catchUpTimedOut.Value || _catchUpArmed) return;
        RestartCatchUp();
    }

    void StopCatchUp() {
        _catchUpArmed = false;
        _catchUpGeneration++;
        _catchUpTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_catchUpTimedOut.Value) _catchUpTimedOut.OnNext(false);
    }

    void RestartCatchUp() {
        if (_disposed) return;
        _catchUpTimedOut.OnNext(false);
        _catchUpArmed = true;
        _catchUpGeneration++;
        _catchUpDeadline = _time.GetUtcNow().Add(CatchUpLimit);
        _catchUpTimer.Change(CatchUpLimit, Timeout.InfiniteTimeSpan);
    }

    void OnCatchUpElapsed() {
        var generation = _catchUpGeneration;
        RxSchedulers.MainThreadScheduler.Schedule(() => {
            if (_disposed || generation != _catchUpGeneration || !_catchUpArmed) return;
            if (_time.GetUtcNow() < _catchUpDeadline) return;
            _awaitingServerAfterSignIn.OnNext(false);
            _catchUpTimedOut.OnNext(true);
        });
    }

    /// Local attach state is checked FIRST — the upstream word is only meaningful once the attach
    /// is Connected (a stale retained snapshot might carry any word). Unknown upstream words read
    /// as disconnected, matching the daemon's own catch-all spelling.
    internal static LaunchAvailability AvailabilityFor(AttachStatus status, string daemonConnection) => status.State switch {
        AttachState.Connecting  => LaunchAvailability.Pending,
        AttachState.Unreachable => LaunchAvailability.DaemonUnavailable,
        _ => daemonConnection switch {
            "connected"                    => LaunchAvailability.Ready,
            "connecting" or "reconnecting" => LaunchAvailability.Pending,
            _                              => LaunchAvailability.ServerDisconnected,
        },
    };

    /// `selectedAvailability` is whichever of AvailabilityFor/RemoteAvailabilityFor applies to the
    /// CURRENT selection — the same value canLaunch gates on, so the notice a remote pick shows
    /// never disagrees with whether Start is actually enabled. `status`/`daemonConnection` matter
    /// only for a local selection's daemon-down/incompatible text — a remote pick has no local
    /// daemon affordance to name, so those never apply to it (a lost lane there is always the
    /// generic ServerLostNotice/ConnectingNotice, matching RemoteAvailabilityFor's own vocabulary).
    /// For a local selection, lane is Dormant unless its server matches the daemon's.
    internal static string? NoticeFor(
            AttachStatus status, string daemonConnection, bool signInExpired, bool awaitingServer,
            bool remoteSelected, LaunchAvailability selectedAvailability,
            ServerLaneState lane = ServerLaneState.Dormant, string? foreignServerNotice = null) {
        if (signInExpired) return SignInExpiredNotice;

        if (!remoteSelected && status.State == AttachState.Unreachable)
            return status.Reason switch {
                IncompatibleReason => DaemonIncompatibleNotice,
                ExpectedDaemonRestart.Reason => DaemonRestartingNotice,
                _ => DaemonDownNotice,
            };

        if (!remoteSelected && status.State == AttachState.Connected && foreignServerNotice is not null) return foreignServerNotice;

        if (awaitingServer && selectedAvailability is LaunchAvailability.ServerDisconnected or LaunchAvailability.Pending)
            return FinishingSignInNotice;

        if (!remoteSelected && status.State == AttachState.Connected && daemonConnection == DaemonSignedOutWord)
            return DaemonSignedOutNotice;

        if (!remoteSelected && selectedAvailability == LaunchAvailability.ServerDisconnected && LaneIsCatchingUp(lane))
            return ConnectingNotice;

        if (remoteSelected)
            return selectedAvailability switch {
                LaunchAvailability.Ready   => null,
                LaunchAvailability.Pending => ConnectingNotice,
                _                          => ServerLostNotice,
            };

        return selectedAvailability switch {
            LaunchAvailability.Ready             => null,
            LaunchAvailability.Pending           => ConnectingNotice,
            LaunchAvailability.DaemonUnavailable => DaemonDownNotice,
            _                                    => ServerLostNotice,
        };
    }

    internal static bool LaneIsCatchingUp(ServerLaneState lane) =>
        lane is ServerLaneState.Connecting or ServerLaneState.Retrying or ServerLaneState.Connected;

    /// A failure is something that broke: versions that cannot work together, a server that refused
    /// the app, or a daemon still down after the lifecycle settled on a report, which only follows a
    /// start or setup that did not work.
    internal static bool BannerFailedFor((string? Message, string? Detail) content) =>
        content.Message is DaemonIncompatibleNotice or LocalServerRefusedNotice
        || (content.Message == DaemonDownNotice && content.Detail is { } detail && !InFlight(detail));

    static bool BannerInFlight((string? Message, string? Detail) content) =>
        content.Detail is { } detail ? InFlight(detail)
            : BusyNotice(content.Message) || InFlight(content.Message) || content.Message == DaemonRestartingNotice;

    /// A lifecycle status still waiting on something, which the banner shows with a spinner. Every
    /// in-flight status ends in an ellipsis; no failure does.
    internal static bool InFlight(string? line) => line?.EndsWith('…') == true;

    internal static bool BusyNotice(string? notice) =>
        notice is ConnectingNotice or FinishingSignInNotice;

    /// A Connecting/Finishing line that outlived CatchUpLimit is a lost session, not catch-up.
    internal static string? NoticeAfterCatchUp(string? notice, bool timedOut) =>
        timedOut && BusyNotice(notice) ? ServerLostNotice : notice;

    /// A local server takes no sign-in, so none of its notices asks for one. With this Mac selected,
    /// the Start banner is the whole story while the server is down, so the generic connecting and
    /// lost lines would contradict it; the daemon's own notices still stand. A remote pick
    /// (<paramref name="remoteMachine"/>) keeps its own notices, naming the machine when it is lost.
    internal static string? NoticeForLocalServer(string? notice, bool localServer, bool localServerDown, string? remoteMachine) =>
        !localServer ? notice : (notice, remoteMachine) switch {
            (SignInExpiredNotice or DaemonSignedOutNotice, _)           => LocalServerRefusedNotice,
            (ServerLostNotice, { } machine)                             => RemoteOfflineNotice(machine),
            (_, not null)                                               => notice,
            (ConnectingNotice or ServerLostNotice, _) when localServerDown => null,
            (ServerLostNotice, _)                                       => LocalServerLostNotice,
            _                                                           => notice,
        };

    /// Repo gate first (IsEnabled), then the connection/sign-in notice StartCommand also gates on,
    /// then an account still resolving.
    internal static string TipFor(string? repoPath, string? connectionNotice, string? accountNotice) =>
        string.IsNullOrEmpty(repoPath) ? "Select a repository to start"
        : connectionNotice ?? accountNotice ?? "Start";

    // Constructor-scoped (like TrayViewModel/ActivityViewModel), not WhenActivated — the OAPH and
    // the Agents subscription above run for this object's whole lifetime, not a window's.
    public void Dispose() {
        _disposed = true;
        _disposables.Dispose();
        Notices.Dispose();
        _daemonStartMessageFeed.Dispose();
        _daemonStartVisibleFeed.Dispose();
        _daemonRetryVisibleFeed.Dispose();
        _daemonRepairVisibleFeed.Dispose();
        _uploadingChanges.Dispose();
        _updateFinishing.Dispose();
        _retentionTimer.Dispose();
        _catchUpTimer.Dispose();
        _catchUpTimedOut.Dispose();
        Tray.Clear();
        lock (_launchTrackingLock) _retainedDraft = null;
    }

    /// The machine half of the gate is asked of the machine a launch would target, not of whatever
    /// is selected now — StartAsync re-runs it against its captured draft after the upload.
    internal static bool CanAttachTo(
            bool signedIn, IReadOnlyList<string>? localCapabilities, IReadOnlyList<DaemonInfo> daemons,
            string? viewerId, string machine, bool remote) =>
        signedIn && (remote
            ? LaunchAttachments.IsCapable(FindMachine(daemons, machine, viewerId)?.Version)
            : localCapabilities is { } caps && caps.Contains(LocalFrameChatInput.AttachCapability));

    internal static string? AttachHintFor(bool signedIn, bool canAttach) =>
        !signedIn ? SignInToAttach : canAttach ? null : LocalFrameChatInput.DaemonNeedsAttachments;

    bool CanAttachFor(LaunchDraft draft) =>
        CanAttachTo(_signedIn, _currentCapabilities, _currentDaemons, _lastViewerId, draft.Machine, draft.Remote);

    /// What every intake source hands its result to: the tray takes what it can, and the rest
    /// becomes one line the launcher shows until the next edit or intake.
    public void Accept(IntakeResult result) {
        ClearIntakeNotice();
        var refused = new List<IntakeRefusal>(result.Refused);
        refused.AddRange(Tray.AddAll(result.Accepted));
        if (RefusalNotice.Render(refused) is not { } notice) return;
        _intakeNotice = notice;
        StartError = notice;
    }

    void ClearIntakeNotice() {
        if (_intakeNotice is null) return;
        if (StartError == _intakeNotice) StartError = null;
        _intakeNotice = null;
    }

    /// Sets the selection and persists it for SelectedRepoPath — except in remote mode, where the
    /// choice applies for the session only; a remote repository is never written into the local
    /// (this-machine-scoped) store.
    public async Task ChooseHarnessAsync(string vendor) {
        SetVendor(vendor);
        if (RemoteMachineSelected) return;

        var repoPath = SelectedRepoPath;
        await _state.UpdateAsync(s => s with { HarnessByRepo = WithEntry(s.HarnessByRepo, repoPath, vendor) });
    }

    /// The repository chip's menu, assembled per open rather than kept as a live projection — the
    /// flyout is transient, so reading at click time is always fresh with no extra subscription.
    /// Sources: remembered HarnessByRepo keys, distinct agent RepoPaths, the daemon's persisted
    /// known repos (what the server's launch dialog sees), and the current selection (a
    /// picker-added repo with no remembered harness and no agent yet lives nowhere else).
    /// Deduped under PathComparer with remembered keys added first, so where two casings are one
    /// repository the casing the user picked is the one displayed. Scratch ("No repository") is
    /// offered only when that list is empty — with real repos, an empty selection adopts the
    /// most recently used known path instead.
    ///
    /// A remote machine selection swaps this entirely: that machine's advertised RepoPaths as the
    /// registry reports them now (none once it is gone, or only a local twin of the same name is
    /// left), plus a typed path not yet among them (no known-repos merge, no
    /// scratch adoption — a remote path is never verified locally), each labelled DefaultVendor
    /// since there is no per-repo remembered harness for a machine that is not this one.
    public async Task<IReadOnlyList<RepositoryOption>> ListRepositoriesAsync() {
        if (RemoteMachineSelected) {
            var remoteSelected = SelectedRepoPath;
            var live = _currentDaemons.Where(d => !IsLocalTwin(d)).ToList();
            var remotePaths = (FindMachine(live, SelectedMachine, _lastViewerId)?.RepoPaths ?? []).ToList();
            if (remoteSelected.Length > 0 && !remotePaths.Contains(remoteSelected, RemotePathComparer))
                remotePaths.Add(remoteSelected);
            return remotePaths
                .OrderBy(RepoLabel.Leaf, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p, StringComparer.Ordinal)
                .Select(p => new RepositoryOption(p, DefaultVendor, RemotePathComparer.Equals(p, remoteSelected)))
                .ToList();
        }

        await EnsureDefaultRepositoryAsync();

        var (byRepo, paths) = await GatherLocalRepoDataAsync();

        var selected = SelectedRepoPath;
        var options = paths
            .OrderBy(RepoLabel.Leaf, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => new RepositoryOption(p, Lookup(byRepo, p) ?? DefaultVendor, PathComparer.Equals(p, selected)))
            .ToList();

        if (paths.Count == 0)
            options.Add(new RepositoryOption(
                ScratchRepoPath, Lookup(byRepo, ScratchRepoPath) ?? DefaultVendor, selected.Length == 0));
        return options;
    }

    /// The path-gathering half of ListRepositoriesAsync's local branch, split out so
    /// ListMachinesAsync can reuse it for the local MachineOption's RepoPaths without a second
    /// AppStateStore read of the same file.
    async Task<(IReadOnlyDictionary<string, string>? ByRepo, List<string> Paths)> GatherLocalRepoDataAsync() {
        var byRepo = (await _state.LoadAsync()).HarnessByRepo;
        var known = await _knownRepos();

        var seen = new HashSet<string>(PathComparer);
        var paths = new List<string>();
        void Add(string? path) {
            if (string.IsNullOrEmpty(path)) return;
            var normalized = PlatformPaths.Normalize(path);
            if (seen.Add(normalized)) paths.Add(normalized);
        }

        foreach (var key in byRepo?.Keys ?? [])
            Add(key);
        // An agent's RepoPath can be a worktree checkout (review flows launch into the
        // requester's worktree) — the menu offers the repository, never the checkout.
        var agentPaths = _daemon.Agents.Items.Select(agent => agent.RepoPath)
            .OfType<string>().Where(path => path.Length > 0).Distinct(PathComparer).ToArray();
        var roots = await Task.Run(() => agentPaths.Select(GitRepository.ResolveMainRepoRoot).ToArray());
        foreach (var root in roots) Add(root);
        foreach (var repo in known)
            Add(repo);
        Add(SelectedRepoPath);

        return (byRepo, paths);
    }

    /// The launcher's machine chip menu: local first, always, then the viewer's own connected
    /// remote daemons — filtered through the one ownership definition (OwnDaemonsAsync), so a row
    /// is never offered for a daemon the viewer doesn't own AT LISTING TIME. That filter can go
    /// stale the moment it's read, so it is the UI affordance layer only: StartAsync re-verifies
    /// ownership fresh, immediately before the wire request is built, and is the actual boundary.
    /// A null viewer id (signed out, or claims not readable yet) means no remote options ever,
    /// never a guess.
    public async Task<IReadOnlyList<MachineOption>> ListMachinesAsync() {
        var (_, localPaths) = await GatherLocalRepoDataAsync();
        var options = new List<MachineOption> {
            new(_daemon.DaemonName, IsLocal: true, _currentAvailability == LaunchAvailability.Ready,
                Platform: null, localPaths.ToArray(), SupportedVendors: null, Selected: !RemoteMachineSelected,
                VendorModels: _currentLocalCatalog),
        };

        foreach (var d in await OwnRemoteDaemonsAsync())
            options.Add(new MachineOption(
                d.Name, IsLocal: false, d.Connected, d.Platform, d.RepoPaths ?? [], d.SupportedVendors,
                Selected: RemoteMachineSelected && string.Equals(d.Name, SelectedMachine, StringComparison.Ordinal),
                Version: d.Version, VendorModels: VendorModelMaps.FromRegistry(d.VendorModels),
                VendorModelsByAccount: VendorModelMaps.FromRegistry(d.VendorModelsByAccount)));

        return options;
    }

    /// isLocal comes from the clicked MachineOption's own IsLocal, never re-derived from name
    /// equality here — an owned REMOTE daemon can be named identically to the local one (they
    /// live on different servers), so matching by name alone would make that remote entry
    /// unselectable. Picking local restores the local repository/vendor flow (revalidated below)
    /// for every non-machine-picking caller; picking a remote name switches the repository and
    /// harness sources to that machine's own advertised set and resets the vendor off one it
    /// cannot host. A remote name that fails the ownership filter is refused outright — the
    /// selection is left exactly as it was, the same as clicking a menu row that was never offered.
    public async Task SelectMachineAsync(string daemonName, bool isLocal) {
        if (isLocal) {
            var wasRemote = RemoteMachineSelected;
            SetMachineSelection(daemonName, remote: false);
            _remoteVendorOverride.OnNext(null);
            if (!wasRemote) return; // already local — nothing a remote pick could have left behind

            // A remote machine's path/vendor can never carry over into the local menu — restore
            // exactly the flow a fresh, no-selection launcher would run.
            SelectedRepoPath = ScratchRepoPath;
            await EnsureDefaultRepositoryAsync();
            RevalidateVendorAgainst(_currentLocalVendors);
            return;
        }

        DaemonInfo? match = null;
        foreach (var d in await OwnRemoteDaemonsAsync())
            if (string.Equals(d.Name, daemonName, StringComparison.Ordinal)) { match = d; break; }
        if (match is null) return; // not one of the viewer's own daemons — never guess ownership

        var machine = new MachineOption(match.Name, false, match.Connected, match.Platform,
            match.RepoPaths ?? [], match.SupportedVendors, true, match.Version, VendorModelMaps.FromRegistry(match.VendorModels),
            VendorModelMaps.FromRegistry(match.VendorModelsByAccount));

        SetMachineSelection(daemonName, remote: true);
        _remoteVendorOverride.OnNext(machine.SupportedVendors);
        SelectedRepoPath = machine.RepoPaths.FirstOrDefault() is { Length: > 0 } first ? first : ScratchRepoPath;
        RevalidateVendorAgainst(machine.SupportedVendors);
    }

    /// Resets SelectedVendor to the first vendor Available in supportedVendors' Build() when the
    /// current one isn't — shared so a machine's OWN advertised set is what a selection (either
    /// direction) is checked against, never whatever a PRIOR machine last set it to.
    void RevalidateVendorAgainst(string[]? supportedVendors) => RevalidateVendor(HostedHarnessCatalog.Build(supportedVendors));

    void RevalidateVendor(IReadOnlyList<HarnessOption> options) {
        var current = options.FirstOrDefault(o => string.Equals(o.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase));
        if (current is not { Available: true })
            SetVendor(options.FirstOrDefault(o => o.Available)?.Vendor ?? DefaultVendor);
    }

    void SetMachineSelection(string name, bool remote) {
        var changed = !string.Equals(SelectedMachine, name, StringComparison.Ordinal)
            || RemoteMachineSelected != remote;
        SelectedMachine = name;
        RemoteMachineSelected = remote;
        _machineSelectionChanges.OnNext((name, remote));
        // Same path can exist on two machines; a refusal belongs to the machine that launched.
        if (changed && StartError != _intakeNotice) StartError = null;
    }

    /// A daemon with the local machine id and the local daemon's own name is this same daemon's
    /// registry entry, not a distinct remote target — MachineId is null-guarded so an app that has
    /// never learned its own machine id (localMachineId: null) never treats a name collision as a
    /// twin (LocalDaemonTwin's identical guard).
    bool IsLocalTwin(DaemonInfo d) =>
        _localMachineId is not null && d.MachineId == _localMachineId
        && string.Equals(d.Name, _daemon.DaemonName, StringComparison.Ordinal);

    /// The one ownership filter: every remote daemon this ViewModel ever offers or resolves by
    /// name goes through this, so "the viewer's own daemons" has exactly one definition. Also
    /// caches the resolved id in _lastViewerId, which FindMachine reads to re-verify ownership
    /// synchronously — the CanExecute pipeline cannot itself await viewerId on every tick.
    async Task<IReadOnlyList<DaemonInfo>> OwnDaemonsAsync(IReadOnlyList<DaemonInfo> all) {
        var viewer = await _viewerId(_shutdown);
        _lastViewerId = viewer;
        _viewerIds.OnNext((true, viewer));
        if (viewer is null) return [];
        return all.Where(d => d.OwnerUserId == viewer && !IsLocalTwin(d)).ToList();
    }

    async Task<IReadOnlyList<DaemonInfo>> OwnRemoteDaemonsAsync() => await OwnDaemonsAsync(await _daemons.FirstAsync());

    /// Live lookup by name AND owner against the raw (unfiltered) daemons list, for the CanExecute
    /// pipeline. A name that ListMachinesAsync/SelectMachineAsync ownership-filtered earlier is
    /// re-checked against viewerId here too: if a later registry update reassigns that same name
    /// to a different owner, the match fails and the selection stops being launchable, rather than
    /// trusting a filter that only ran once, at selection time. A null viewerId (never resolved,
    /// or resolved to signed-out) means no remote daemon is ever treated as owned.
    static MachineOption? FindMachine(IReadOnlyList<DaemonInfo> daemons, string name, string? viewerId) =>
        FindDaemon(daemons, name, viewerId) is { } d
            ? new MachineOption(d.Name, false, d.Connected, d.Platform, d.RepoPaths ?? [], d.SupportedVendors, true, d.Version,
                VendorModelMaps.FromRegistry(d.VendorModels), VendorModelMaps.FromRegistry(d.VendorModelsByAccount))
            : null;

    static DaemonInfo? FindDaemon(IReadOnlyList<DaemonInfo> daemons, string name, string? viewerId) {
        if (viewerId is null) return null;
        foreach (var d in daemons)
            if (string.Equals(d.Name, name, StringComparison.Ordinal) && d.OwnerUserId == viewerId)
                return d;
        return null;
    }

    /// Unknown until this connection's listing has answered for the daemon: a listing that answered
    /// without it is still Unknown, never Unsupported. The local daemon is its twin row whatever owner
    /// that row carries, since the viewer's listing includes a daemon still registered under a
    /// claimed legacy id. Without a twin (a daemon that predates machine ids lists none) it is
    /// matched by name and owner, or, on a server without sign-in, as the one row carrying its name.
    AccountSupport SupportFor(DaemonListing listing, string machine, string? viewerId, bool remote) {
        if (listing.Daemons is not { } daemons) return AccountSupport.Unknown;

        var daemon = remote ? FindDaemon(daemons, machine, viewerId)
            : OnlyRow(daemons, IsLocalTwin) ?? (viewerId is null
                ? OnlyRow(daemons, d => string.Equals(d.Name, machine, StringComparison.Ordinal))
                : FindDaemon(daemons, machine, viewerId));
        return daemon is null ? AccountSupport.Unknown
            : daemon.SupportsAccountLaunches ? AccountSupport.Supported
            : AccountSupport.Unsupported;
    }

    /// Two matching rows cannot be told apart, so they match nothing.
    static DaemonInfo? OnlyRow(IReadOnlyList<DaemonInfo> daemons, Func<DaemonInfo, bool> matches) {
        DaemonInfo? match = null;
        foreach (var d in daemons) {
            if (!matches(d)) continue;
            if (match is not null) return null;
            match = d;
        }
        return match;
    }

    /// A remote launch is Ready only with the app's OWN server lane connected AND the selected
    /// daemon's latest reported Connected — the local daemon-down notices never apply here (they
    /// stay local-only; a remote pick with the lane down still just shows ServerLostNotice).
    /// Connecting and Retrying read as Pending so a remote pick can distinguish catch-up from a
    /// lost session — Retrying is the normal redial, not a prompt for Sign in.
    internal static LaunchAvailability RemoteAvailabilityFor(ServerLaneStatus lane, MachineOption? machine) {
        if (lane.State is ServerLaneState.Connecting or ServerLaneState.Retrying)
            return LaunchAvailability.Pending;
        if (lane.State != ServerLaneState.Connected) return LaunchAvailability.ServerDisconnected;
        return machine is { Connected: true } ? LaunchAvailability.Ready : LaunchAvailability.DaemonUnavailable;
    }

    /// When nothing is selected, adopt the most recently used known repository (then a remembered
    /// harness key, then an agent root). No-op when a selection already exists or nothing is known,
    /// and for a remote machine, which these local paths do not describe.
    public async Task EnsureDefaultRepositoryAsync() {
        if (SelectedRepoPath.Length > 0 || RemoteMachineSelected) return;
        if (await PreferRecentRepositoryAsync() is not { Length: > 0 } recent) return;
        // PreferRecent can await file I/O — a pick or a machine switch made while that ran must win.
        if (SelectedRepoPath.Length > 0 || RemoteMachineSelected) return;
        await SelectRepositoryAsync(recent);
    }

    async Task<string?> PreferRecentRepositoryAsync() {
        foreach (var path in await _knownRepos()) {
            var normalized = PlatformPaths.Normalize(path);
            if (normalized.Length > 0) return normalized;
        }

        foreach (var key in (await _state.LoadAsync()).HarnessByRepo?.Keys ?? []) {
            var normalized = PlatformPaths.Normalize(key);
            if (normalized.Length > 0) return normalized;
        }

        var agentPaths = _daemon.Agents.Items.Select(agent => agent.RepoPath)
            .OfType<string>().Where(path => path.Length > 0).Distinct(PathComparer).ToArray();
        return await Task.Run(() => agentPaths.Select(path => PlatformPaths.Normalize(GitRepository.ResolveMainRepoRoot(path)))
            .FirstOrDefault(path => path.Length > 0));
    }

    /// Sets the repository and restores that repository's remembered harness, or DefaultVendor
    /// when none — never the vendor a DIFFERENT repository had selected. In remote mode the local
    /// HarnessByRepo store is never consulted. Either way the result is revalidated against the
    /// selected machine's current harnesses.
    public async Task SelectRepositoryAsync(string repoPath) {
        // This host's separator rules would rewrite a remote path: on Unix, Windows' C:/ trims to C:.
        SelectedRepoPath = repoPath.Length == 0 ? ScratchRepoPath
            : RemoteMachineSelected ? repoPath
            : PlatformPaths.Normalize(repoPath);
        if (RemoteMachineSelected) {
            RevalidateVendor(Harnesses);
            return;
        }

        var selected = SelectedRepoPath;
        var saved = await _state.LoadAsync();
        // A machine switch or another pick made while the store loaded owns the vendor now.
        if (RemoteMachineSelected || !string.Equals(SelectedRepoPath, selected, StringComparison.Ordinal)) return;
        SetVendor(Lookup(saved.HarnessByRepo, SelectedRepoPath) ?? DefaultVendor);
        RevalidateVendor(Harnesses);
    }

    /// A remote machine's repository is typed rather than picked: the local folder picker would hand
    /// it a path from this machine. Returns the reason a path was refused, or null once it is
    /// selected. Only shape is checked; existence and the daemon's allowed-repo list are judged by
    /// the daemon at launch.
    public async Task<string?> EnterRemoteRepositoryAsync(string path) {
        var trimmed = path.Trim();
        if (!RemoteMachineSelected) return "Pick a remote machine first.";
        if (!LooksAbsolute(trimmed)) return $"Enter the full path of the repository on {SelectedMachine}.";
        await SelectRepositoryAsync(trimmed);
        return null;
    }

    /// Absolute on any platform the remote machine might run, which need not be this one's.
    internal static bool LooksAbsolute(string path) =>
        path.StartsWith('/')
     || path.StartsWith(@"\\", StringComparison.Ordinal)
     || path is [_, ':', '\\' or '/', ..] && char.IsAsciiLetter(path[0]);

    /// Read against the draft's machine, not the current selection, which may have moved during an upload.
    string? HarnessErrorFor(LaunchDraft draft) {
        var options = HostedHarnessCatalog.Build(draft.Remote
            ? FindMachine(_currentDaemons, draft.Machine, _lastViewerId)?.SupportedVendors
            : _currentLocalVendors);
        var harness = options.FirstOrDefault(o => string.Equals(o.Vendor, draft.Vendor, StringComparison.OrdinalIgnoreCase));
        if (harness is { Available: true }) return null;
        return options.Any(o => o.Available)
            ? HarnessUnavailableMessage(harness?.Label ?? draft.Vendor)
            : NoHarnessesNotice(draft.Remote ? draft.Machine : null);
    }

    /// The one place a vendor change lands, so the model-reset invariant (ids are
    /// vendor-specific) can never be forgotten by a new call site.
    void SetVendor(string vendor) {
        if (vendor != SelectedVendor) {
            SelectedModel = "";
            // An account refusal names the previous vendor's account, and its Use default would reset this one.
            if (StartErrorOffersDefault) StartError = null;
        }
        SelectedVendor = vendor;
    }

    /// EnterTarget runs only on a real change of repository, vendor or machine, since a repeated
    /// call would drop the person's pick; nothing is published until support and snapshot follow.
    void ApplyAccountInputs(
            (string Name, bool Remote) sel, string repoPath, string vendor, DaemonListing listing,
            IReadOnlyList<DaemonAccountsDto> remoteAccounts, AccountsSnapshotDto? localAccounts, (bool Resolved, string? Id) viewer) {
        var machine = AccountMachine(sel.Name, sel.Remote);
        _applyingAccountInputs = true;
        try {
            if (_accountTarget is not { } last || !PathComparerFor(sel.Remote).Equals(last.RepoPath, repoPath)
             || last.Vendor != vendor || last.Machine != machine || last.Remote != sel.Remote) {
                _accountTarget = (repoPath, vendor, machine, sel.Remote);
                _accountChooser.EnterTarget(repoPath, vendor);
            }
            var support = viewer.Resolved ? SupportFor(listing, machine, viewer.Id, sel.Remote) : AccountSupport.Unknown;
            var snapshot = AccountsSnapshotFor(sel.Remote, machine, remoteAccounts, localAccounts);
            // A remote host's accounts arrive only by the server's fetch, so a missing row is retried like the listing.
            var retryable = listing.FetchFailed || (sel.Remote && support == AccountSupport.Supported && snapshot is null);
            _accountChooser.SetSupport(support, retryable);
            _accountChooser.SetSnapshot(snapshot);
        } finally {
            _applyingAccountInputs = false;
        }
        PublishAccountChoice();
    }

    static IReadOnlyDictionary<string, AccountUsageView> ProjectUsage(AccountsSnapshotDto? snapshot, string vendor, DateTimeOffset now) =>
        snapshot is null
            ? FrozenDictionary<string, AccountUsageView>.Empty
            : (snapshot.Accounts ?? [])
                .Where(a => string.Equals(a.Vendor, vendor, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(a => a.Id)
                .ToFrozenDictionary(a => a.Id, a => AccountUsage.Project(a, now, TimeZoneInfo.Local));

    internal static bool UsageEquals(IReadOnlyDictionary<string, AccountUsageView> a, IReadOnlyDictionary<string, AccountUsageView> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.Equals(other));

    /// Equality over what the launcher draws: age and readiness do not change its rows.
    internal static bool DrawnUsageEquals(IReadOnlyDictionary<string, AccountUsageView> a, IReadOnlyDictionary<string, AccountUsageView> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.DrawnEquals(other));

    string AccountMachine(string name, bool remote) => remote ? name : _daemon.DaemonName;

    static AccountsSnapshotDto? AccountsSnapshotFor(
            bool remote, string machine, IReadOnlyList<DaemonAccountsDto> remoteAccounts, AccountsSnapshotDto? localAccounts) =>
        remote ? remoteAccounts.FirstOrDefault(d => d.DaemonName == machine)?.ToSnapshot() : localAccounts;

    void PublishAccountChoice() {
        if (_applyingAccountInputs) return;
        var chooser = _accountChooser;
        AccountSection = chooser.ShowsSection;
        AccountRows = chooser.Rows;
        AccountPendingText = chooser.PendingText;
        AccountRetryVisible = chooser.OffersRetry;
        SelectedAccountLabel = chooser.SelectedLabel;
        _accountPending.OnNext(chooser.State == AccountSelectionState.Pending);
        _accountTip.OnNext(chooser.PendingText);
        // Invalid keeps the key too: a reconnect that briefly unsettles support must not reset a model
        // only the account lists.
        _accountCatalogKey.OnNext(chooser.State is AccountSelectionState.ExplicitValid or AccountSelectionState.ExplicitInvalid
            && _accountTarget is { } target ? (target.Vendor, chooser.SelectedAccountId!) : null);
    }

    /// Written in call order: the chooser also forgets an entry from inside its recompute.
    void RememberAccount(string hostId, string vendor, string repoPath, string? accountId) {
        _accountMemory = AccountMemory.With(_accountMemory, hostId, vendor, repoPath, accountId);
        _accountMemoryWrites = WriteAfterAsync(_accountMemoryWrites);

        async Task WriteAfterAsync(Task previous) {
            await previous.ConfigureAwait(false);
            await _state.UpdateAsync(s => s with {
                AccountByRepo = AccountMemory.With(s.AccountByRepo, hostId, vendor, repoPath, accountId),
            }).ConfigureAwait(false);
        }
    }

    /// The Default-badged row follows the daemon's default rather than pinning its account. Any
    /// pick answers an account refusal. The task completes once the choice is persisted.
    public Task PickAccountAsync(string? accountId) {
        var defaultRow = _accountChooser.Rows.Any(c => c.Row.IsDefault && c.Row.Id == accountId);
        _accountChooser.Pick(defaultRow ? null : accountId);
        if (StartErrorOffersDefault) StartError = null;
        return _accountMemoryWrites;
    }

    void UseDefaultAccount() {
        _accountChooser.UseDefault();
        StartError = null;
    }

    void RefuseAccount(string reason) {
        StartError = $"{reason}. Use the default account or pick another.";
        StartErrorOffersDefault = true;
    }

    /// Read against the draft's target and the latest listing and snapshot, never the chooser,
    /// which follows the live selection.
    string? AccountErrorFor(LaunchDraft draft, string? label) {
        if (draft.AccountId is not { } id) return null;

        var machine = AccountMachine(draft.Machine, draft.Remote);
        var snapshot = AccountsSnapshotFor(draft.Remote, machine, _currentDaemonAccounts, _currentLocalAccounts);
        var listed = snapshot is not null && Services.AccountRows.For(snapshot, draft.Vendor).Any(r => r.Id == id);
        return listed && SupportFor(_currentListing, machine, _lastViewerId, draft.Remote) == AccountSupport.Supported
            ? null
            : AccountChooser.UnavailableReason(label ?? id);
    }

    /// The selected account's own list replaces its vendor's entry where the machine has one for it.
    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> WithAccountCatalog(
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> merged,
            IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? byAccount, (string Vendor, string AccountId)? account) {
        if (account is not { } chosen || byAccount is null || !byAccount.TryGetValue(chosen.AccountId, out var models)) return merged;
        return new Dictionary<string, IReadOnlyList<ModelChoice>>(merged, StringComparer.OrdinalIgnoreCase) { [chosen.Vendor] = models };
    }

    async Task StartAsync() {
        // A pending account captures no draft: sending one would launch under a default nobody chose.
        if (!_accountChooser.IsResolved) {
            if (_accountChooser.State == AccountSelectionState.ExplicitInvalid) RefuseAccount(_accountChooser.InvalidReason!);
            return;
        }

        // Captured before anything can await: an upload takes time the user can spend re-pointing
        // the launcher, and the files must reach the machine the composer was aimed at.
        var draft = new LaunchDraft(
            SelectedMachine, RemoteMachineSelected, SelectedRepoPath, SelectedVendor, Goal, _goalEdits,
            SelectedModel, SelectedEffort, PermissionModeFor(SelectedVendor, SelectedPermissionMode),
            CodexPostureFor(SelectedVendor, SelectedCodexSandbox, SelectedCodexApproval),
            Tray.Snapshot(), Tray.Generation,
            _accountChooser.State == AccountSelectionState.ExplicitValid ? _accountChooser.SelectedAccountId : null);
        var accountLabel = _accountChooser.SelectedLabel;

        if (draft.Files.Count > 0 && !CanAttachFor(draft)) {
            StartError = AttachHintFor(_signedIn, false);
            return;
        }

        // Execute() does not gate on CanExecute, and a remembered vendor restored with a repository
        // is never checked against what the machine hosts.
        if (HarnessErrorFor(draft) is { } harnessError) {
            StartError = harnessError;
            return;
        }

        // ReactiveCommand.Execute() does not itself gate on CanExecute — a caller that bypasses
        // the bound Button (or the canExecute observable's own staleness, however small) can still
        // reach here, so a remote target gets one more, fully fresh ownership+connected check
        // right before the wire request is built. This is the actual boundary; canLaunch/
        // FindMachine above are the UI-responsive affordance, not a substitute for it.
        if (draft.Remote && !await OwnsConnectedAsync(draft.Machine)) {
            StartError = MachineUnavailableMessage;
            return;
        }

        IReadOnlyList<string>? attachmentIds = null;
        DateTimeOffset? uploadedAt = null;
        if (draft.Files.Count > 0) {
            Uploading = true;
            UploadOutcome upload;
            // The app closing mid-upload is not a launch failure, and the command's only subscriber
            // is the Enter key: a fault here would surface as an unhandled command exception.
            try { upload = await _uploader.UploadAsync(draft.Files, _shutdown); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
            finally { Uploading = false; }
            if (upload.Kind == UploadKind.Unauthorized) {
                _signInRequired.OnNext(true);
                StartError = SignInToAttach;
                return;
            }
            if (upload.Kind != UploadKind.Uploaded) {
                StartError = upload.Reason ?? "the upload failed";
                return;
            }
            attachmentIds = upload.Ids;
            uploadedAt = _time.GetUtcNow();
            // Both checks run again against the DRAFT: the upload window is long enough for the
            // target to have been revoked or downgraded under it.
            if (draft.Remote && !await OwnsConnectedAsync(draft.Machine)) {
                StartError = MachineUnavailableMessage;
                return;
            }
            if (!CanAttachFor(draft)) {
                StartError = AttachHintFor(_signedIn, false);
                return;
            }
            if (HarnessErrorFor(draft) is { } withdrawn) {
                StartError = withdrawn;
                return;
            }
        }

        // After the upload, or a remote ownership check, the account may have gone from under the draft.
        if (AccountErrorFor(draft, accountLabel) is { } accountError) {
            RefuseAccount(accountError);
            return;
        }

        var request = new LaunchRequest(
            draft.Machine, draft.RepoPath, draft.Vendor, draft.Goal, draft.Model, draft.Effort,
            draft.PermissionMode, attachmentIds, draft.CodexPosture, draft.AccountId);
        // Captured BEFORE the call, never after: the whole point is to notice a navigation that
        // changed WHILE the launch was in flight.
        var generation = _navigationGeneration?.Invoke() ?? 0;

        var outcome = await _launch.StartAsync(request, _shutdown);
        if (!outcome.Started) {
            // Every finished attempt is fresh evidence, so the flag follows it both ways. An
            // unauthorized outcome renders as the sign-in notice, never as raw transport text.
            _signInRequired.OnNext(outcome.Unauthorized);
            if (outcome.Unauthorized)
                StartError = null;
            else if (SameLaunchTarget(draft.RepoPath, draft.Machine, draft.Remote) && outcome.Error is { } error)
                StartError = FriendlyLaunchFailure(error);
            return;
        }

        _signInRequired.OnNext(false);
        StartError = null;
        // The launch really did start, so what it sent is spent — but only what it sent: a goal
        // typed into since, and chips staged since, belong to the next one.
        var clearedGoal = _goalEdits == draft.GoalEdits;
        if (clearedGoal) Goal = "";
        var trayUntouched = Tray.Generation == draft.TrayGeneration;
        Tray.RemoveAll([.. draft.Files.Select(f => f.Id)]);
        var clearedTray = trayUntouched && Tray.Count == 0;

        if (NormalizeAgentId(outcome.AgentId) is not { } agentId) {
            StartError = draft.Files.Count > 0
                ? UnusableIdMessage + " — re-attach the files if it did not start"
                : UnusableIdMessage;
            return;
        }

        // The accepted id is request-accepted, not success — track it until a LaunchFailed or a
        // directory row settles it, holding the sent bytes for that long so a failure can hand the
        // draft back. Retained BEFORE registering: RecordPendingLaunch settles a failure that
        // arrived while the invoke above was still in flight, and that failure gets the same
        // restore a delayed one does.
        if (draft.Files.Count > 0)
            lock (_launchTrackingLock)
                _retainedDraft = new RetainedDraft(
                    agentId, draft, uploadedAt!.Value, clearedGoal, _goalEdits, clearedTray, Tray.Generation);
        var lane = draft.Remote ? AgentOrigin.Remote : AgentOrigin.Local;
        var tracked = RecordPendingLaunch(
            agentId, lane, draft.Files.Count > 0, uploadedAt, draft.RepoPath, generation, draft.Machine);
        if (draft.Remote) {
            if (!tracked) {
                if (RowId(agentId, AgentOrigin.Remote) is { } rowId)
                    _openSessionIfCurrent?.Invoke(rowId, AgentOrigin.Remote, generation);
                return;
            }
            // Keyed by the server's own spelling, which the registry row is expected to share.
            _directory?.AddRemotePlaceholder(outcome.AgentId!, draft.Machine, _lastViewerId, draft.Vendor, draft.RepoPath,
                AgentRow.TitleFromPrompt(draft.Goal), draft.Model);
            _openSessionIfCurrent?.Invoke(outcome.AgentId!, AgentOrigin.Remote, generation);
            // Opening moved the generation on. Should the registry spell the id differently, its
            // row lands under another key, and ConfirmPendingRows still swaps the open placeholder
            // workspace over to it unless the user has navigated since.
            if (_navigationGeneration?.Invoke() is { } opened)
                lock (_launchTrackingLock)
                    if (_pendingLaunches.TryGetValue(agentId, out var pending) && pending.Generation == generation)
                        _pendingLaunches[agentId] = pending with { Generation = opened };
            return;
        }
        if (tracked)
            _directory?.AddPlaceholder(agentId, draft.Vendor, draft.RepoPath, AgentRow.TitleFromPrompt(draft.Goal), draft.Model);
        // Untracked with no row is a failure that beat the call's own return: opening would cover
        // the launcher that has just worded it.
        if (tracked || RowExists(agentId, AgentOrigin.Local))
            _openSessionIfCurrent?.Invoke(agentId, AgentOrigin.Local, generation);
    }

    async Task<bool> OwnsConnectedAsync(string machine) =>
        (await OwnRemoteDaemonsAsync())
        .Any(d => string.Equals(d.Name, machine, StringComparison.Ordinal) && d.Connected);

    /// False when a row or a buffered failure has already settled the launch.
    bool RecordPendingLaunch(
            string agentId, AgentOrigin lane, bool hadAttachments, DateTimeOffset? uploadedAt,
            string repoPath, int generation, string machine) {
        // A row for this id confirms success, and it can appear on either side of the registration
        // below: the launch may have succeeded before this method ran at all, or the directory's
        // Add may land while it runs — at which point ConfirmPendingRows finds nothing pending yet
        // and clears nothing. So the row is checked twice, and a buffered failure only ever renders
        // when neither check found one. Both checks are outside _launchTrackingLock (RowExists
        // takes no lock of its own), keeping the cache→tracking lock order intact.
        if (RowExists(agentId, lane)) {
            ForgetLaunch(agentId);
            return false;
        }

        string? bufferedReason = null;
        lock (_launchTrackingLock) {
            _pendingLaunches[agentId] = new PendingLaunch(
                _time.GetUtcNow(), lane, hadAttachments, uploadedAt, repoPath, generation, machine);
            if (_recentFailures.TryGetValue(agentId, out var recent)) {
                if (_time.GetUtcNow().UtcDateTime - recent.At <= RecentFailureTtl) {
                    bufferedReason = recent.Reason;
                    _pendingLaunches.Remove(agentId);
                }
                _recentFailures.Remove(agentId);
            }
        }
        if (RowExists(agentId, lane)) {
            ForgetLaunch(agentId);
            return false;
        }
        if (bufferedReason is not null)
            ApplyLaunchFailure(agentId, hadAttachments, bufferedReason, repoPath, machine, lane);
        return bufferedReason is null;
    }

    void ForgetLaunch(string agentId) {
        lock (_launchTrackingLock) {
            _pendingLaunches.Remove(agentId);
            _recentFailures.Remove(agentId);
            if (_retainedDraft?.AgentId == agentId) _retainedDraft = null;
        }
    }

    /// Releases what neither a failure nor a row will settle: bytes are held for one window from
    /// the upload, never for the life of the launcher.
    void ReleaseExpired() {
        lock (_launchTrackingLock) {
            if (_retainedDraft is { } retained && _time.GetUtcNow() - retained.UploadedAt > RetentionTtl)
                _retainedDraft = null;
            foreach (var stale in _pendingLaunches.Where(kv => IsExpired(kv.Value)).Select(kv => kv.Key).ToList())
                _pendingLaunches.Remove(stale);
        }
    }

    bool IsExpired(PendingLaunch pending) =>
        pending.UploadedAt is { } uploadedAt
            ? _time.GetUtcNow() - uploadedAt > RetentionTtl
            : _time.GetUtcNow() - pending.At > PendingLaunchTtl;

    // Directory keys preserve the row's incoming id spelling (e.g. a dashed Guid never becomes
    // "local:{N-form}"), so a lookup by the "N"-normalized pending id would miss it — scan and
    // compare under NormalizeAgentId instead, the same comparison ConfirmPendingRows uses.
    bool RowExists(string agentId, AgentOrigin lane) => RowId(agentId, lane) is not null;

    string? RowId(string agentId, AgentOrigin lane) =>
        _directory?.Rows.Items.FirstOrDefault(r => r.Origin == lane && NormalizeAgentId(r.Id) == agentId)?.Id;

    void RecordRecentFailure(LaunchFailure failure) {
        if (NormalizeAgentId(failure.AgentId) is not { } agentId) return;
        lock (_launchTrackingLock) {
            _recentFailures[agentId] = (failure.Reason, _time.GetUtcNow().UtcDateTime);
            var cutoff = _time.GetUtcNow().UtcDateTime - RecentFailureTtl;
            foreach (var stale in _recentFailures.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                _recentFailures.Remove(stale);
        }
    }

    /// A failure for an id nothing here is tracking is another client's launch — ignored. A
    /// pending entry consulted past its 10-minute TTL is treated the same way. Compared under
    /// NormalizeAgentId so a dashed-Guid failure id still matches the "N"-normalized key StartAsync
    /// recorded — the hub returns either shape (NormalizeAgentId's own comment).
    void ApplyFailureIfPending(LaunchFailure failure) {
        if (NormalizeAgentId(failure.AgentId) is not { } agentId) return;
        PendingLaunch? pending = null;
        lock (_launchTrackingLock) {
            if (_pendingLaunches.TryGetValue(agentId, out var entry) && !IsExpired(entry)) pending = entry;
            _pendingLaunches.Remove(agentId);
        }
        if (pending is null) return;
        ApplyLaunchFailure(
            agentId, pending.HadAttachments, failure.Reason, pending.RepoPath, pending.Machine, pending.Lane);
        _directory?.RemovePlaceholder(agentId);
        _launchFailed?.Invoke(agentId);
    }

    /// A text-only launch just renders its reason. A launch that carried files hands the draft
    /// back when the composer is still exactly as this launch left it and still aimed at the same
    /// target — otherwise it says to re-attach, because restoring would overwrite what is there.
    void ApplyLaunchFailure(
            string agentId, bool hadAttachments, string reason, string repoPath, string machine,
            AgentOrigin lane) {
        var friendly = FriendlyLaunchFailure(reason);
        // A text-only refusal names the folder+machine that was launched. Files that were not
        // handed back still need the re-attach line on whatever target is selected now — the
        // bytes left with the failed launch and the user must re-stage them for the next send.
        if (!hadAttachments) {
            if (SameLaunchTarget(repoPath, machine, lane == AgentOrigin.Remote)) StartError = friendly;
            return;
        }

        RetainedDraft? retained;
        lock (_launchTrackingLock) {
            retained = _retainedDraft is { } r && r.AgentId == agentId
                && _time.GetUtcNow() - r.UploadedAt <= RetentionTtl ? r : null;
            if (_retainedDraft?.AgentId == agentId) _retainedDraft = null;
        }

        if (retained is not null && IsRestorable(retained)) {
            Goal = retained.Draft.Goal;
            Tray.Restore(retained.Draft.Files);
            StartError = friendly + " — your draft is back";
            return;
        }
        StartError = friendly + " — re-attach the files to send them again";
    }

    /// Local and remote can share a daemon name and path; RemoteMachineSelected separates them.
    bool SameLaunchTarget(string repoPath, string machine, bool remote) =>
        PathComparerFor(remote).Equals(SelectedRepoPath, repoPath)
        && string.Equals(SelectedMachine, machine, StringComparison.Ordinal)
        && RemoteMachineSelected == remote;

    bool IsRestorable(RetainedDraft retained) =>
        retained.ClearedGoal && _goalEdits == retained.GoalEditsAfterClear
        && retained.ClearedTray && Tray.Generation == retained.TrayGenerationAfterClear
        && retained.Draft.Machine == SelectedMachine
        && retained.Draft.Remote == RemoteMachineSelected
        && retained.Draft.RepoPath == SelectedRepoPath
        && retained.Draft.Vendor == SelectedVendor;

    /// A row for a tracked id is success confirmation: drop the pending entry and any buffered
    /// failure so a later, stale LaunchFailed for the same id cannot override it. Same
    /// NormalizeAgentId comparison as ApplyFailureIfPending, for the same id-shape reason.
    /// A remote launch's row is also its auto-open: the remote workspace needs that row to exist.
    void ConfirmPendingRows(IChangeSet<AgentRow, string> changes) {
        List<(string AgentId, int Generation)>? remoteOpens = null;
        lock (_launchTrackingLock) {
            // A pending row or placeholder is the launch's own stand-in, so only a published row on
            // the launch's own lane settles it. The registry's row can replace a remote placeholder
            // under the same key, which arrives as an update.
            foreach (var change in changes)
                if (change.Reason is ChangeReason.Add or ChangeReason.Update && !change.Current.IsPlaceholder
                    && NormalizeAgentId(change.Current.Id) is { } agentId
                    && _pendingLaunches.TryGetValue(agentId, out var pending) && pending.Lane == change.Current.Origin) {
                    _pendingLaunches.Remove(agentId);
                    _recentFailures.Remove(agentId);
                    if (_retainedDraft?.AgentId == agentId) _retainedDraft = null;
                    if (pending.Lane == AgentOrigin.Remote && !IsExpired(pending))
                        (remoteOpens ??= []).Add((change.Current.Id, pending.Generation));
                }
        }
        // Rows publish off the UI thread, and opening swaps the window's workspace.
        if (remoteOpens is not null && _openSessionIfCurrent is { } open)
            RxSchedulers.MainThreadScheduler.Schedule(() => {
                if (_disposed) return;
                foreach (var (agentId, generation) in remoteOpens) open(agentId, AgentOrigin.Remote, generation);
            });
    }

    /// Shown when an older daemon still refuses an empty standalone snapshot. Current daemons
    /// commit an empty tree instead so a greenfield folder can launch. Wording stays neutral about
    /// whether the folder is a git repo — an initialized commitless repo takes the same path.
    internal const string EmptyFolderStartMessage =
        "There's nothing in this folder to start from.";

    /// Shown when preparing the workspace failed for some other git reason.
    internal const string WorkspacePrepFailed = "Couldn't prepare a workspace for this folder.";

    /// <see cref="WireTokens.LaunchDeniedByOwnerPrefix"/> is a consent-gate denial on the target
    /// machine, and an attachment_unavailable reason is the files failing to reach it. An empty
    /// standalone snapshot and a raw <c>git … failed:</c> line are rewritten; every other reason
    /// passes through (the server already truncates to 400 characters).
    internal static string FriendlyLaunchFailure(string reason) {
        if (reason == WireTokens.LaunchCancelled)
            return "The launch was cancelled.";
        if (reason.StartsWith(WireTokens.LaunchDeniedByOwnerPrefix, StringComparison.Ordinal))
            return "That machine's consent policy denied the launch. Approve it there, or pre-set a rule with kcap consent.";
        if (reason.StartsWith("attachment_unavailable:", StringComparison.Ordinal))
            return "the attached files could not be delivered to the machine";
        if (reason.StartsWith("standalone_snapshot_empty", StringComparison.Ordinal))
            return EmptyFolderStartMessage;
        if (IsEmptySnapshotCommit(reason))
            return EmptyFolderStartMessage;
        if (GitFailureSentence(reason) is { } git)
            return git;
        return reason;
    }

    /// The snapshot commit reports "nothing to commit" on stdout. A daemon that only forwarded
    /// stderr leaves this as a bare command, sometimes with that stdout appended.
    static bool IsEmptySnapshotCommit(string reason) {
        const string prefix = "git commit -m Initial snapshot failed:";
        if (!reason.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var detail = reason[prefix.Length..];
        return string.IsNullOrWhiteSpace(detail)
            || detail.Contains("nothing to commit", StringComparison.Ordinal)
            || detail.Contains("nothing added to commit", StringComparison.Ordinal);
    }

    /// A launch reason that is still a git argv line. The command is not something to show.
    static string? GitFailureSentence(string reason) {
        const string marker = " failed: ";
        if (!reason.StartsWith("git ", StringComparison.Ordinal)) return null;
        var at = reason.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var detail = reason[(at + marker.Length)..].Trim().ReplaceLineEndings(" ");
        if (detail.StartsWith("fatal: ", StringComparison.OrdinalIgnoreCase))
            detail = detail["fatal: ".Length..];
        else if (detail.StartsWith("error: ", StringComparison.OrdinalIgnoreCase))
            detail = detail["error: ".Length..];
        detail = detail.Trim();
        if (detail.Length == 0) return WorkspacePrepFailed;
        if (char.IsLower(detail[0])) detail = char.ToUpperInvariant(detail[0]) + detail[1..];
        return WorkspacePrepFailed + " " + detail;
    }

    /// Null for Manual (the harness's own default) and for any vendor that takes no mode.
    internal static string? PermissionModeFor(string vendor, string mode) =>
        HostedHarnessCatalog.SupportsPermissionMode(vendor)
     && !string.Equals(mode, ClaudePermissionModes.Manual, StringComparison.Ordinal)
            ? mode
            : null;

    /// Null for any vendor but codex, and for the daemon's own default pair, so an untouched
    /// launcher sends no posture and a daemon without posture support still accepts the launch.
    /// Both halves go together once either differs — the daemon rejects a partial posture.
    internal static CodexLaunchPosture? CodexPostureFor(string vendor, string sandbox, string approval) =>
        HostedHarnessCatalog.SupportsCodexPosture(vendor)
     && !(string.Equals(sandbox, HostedHarnessCatalog.DefaultCodexSandbox, StringComparison.Ordinal)
       && string.Equals(approval, HostedHarnessCatalog.DefaultCodexApproval, StringComparison.Ordinal))
            ? new CodexLaunchPosture(sandbox, approval)
            : null;

    /// An id that matches nothing degrades gracefully in the workspace ("session not found" with
    /// retry), which beats a red error under a live card.
    internal static string? NormalizeAgentId(string? agentId) => AgentIds.Normalize(agentId);

    /// Applied on READ because System.Text.Json rebuilds the dictionary with a default (ordinal)
    /// comparer on load — a comparer set only at write time would not survive the round-trip.
    static readonly StringComparer PathComparer = PlatformPaths.Comparer;
    // This host's case rules are not the remote machine's: a Linux remote keeps /w/Repo and /w/repo apart.
    static readonly StringComparer RemotePathComparer = StringComparer.Ordinal;
    static StringComparer PathComparerFor(bool remote) => remote ? RemotePathComparer : PathComparer;

    static string? Lookup(IReadOnlyDictionary<string, string>? byRepo, string repoPath) {
        if (byRepo is null) return null;

        foreach (var entry in byRepo)
            if (PathComparer.Equals(entry.Key, repoPath))
                return entry.Value;

        return null;
    }

    /// Replaces any entry whose key matches under PathComparer, so re-choosing a harness for the
    /// same repository reached under different casing or a trailing separator overwrites rather
    /// than accumulating a second, shadowing entry.
    static IReadOnlyDictionary<string, string> WithEntry(IReadOnlyDictionary<string, string>? existing, string key, string value) {
        var normalized = key.Length == 0 ? key : PlatformPaths.Normalize(key);
        var next = new Dictionary<string, string>();
        if (existing is not null)
            foreach (var entry in existing)
                if (!PathComparer.Equals(entry.Key, normalized))
                    next[entry.Key] = entry.Value;

        next[normalized] = value;
        return next;
    }
}
