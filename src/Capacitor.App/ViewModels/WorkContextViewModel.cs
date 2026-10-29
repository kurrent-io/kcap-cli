using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests.Readers;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum WorkContextPhase { WaitingForSession, Loading, Ready, NoWorkItem, SignedOut, NotInPlan, Unreachable, SessionUnknown }

/// The work-context sidebar for one session: facts from the daemon's dto, the work item from the
/// server. Ctor-scoped; TeardownAsync is the one exit.
///
/// The session id is the read's identity. A lease owns one id, its cancellation and its pending
/// read; a result applies only for the current lease, every lease is kept until its read settles
/// so teardown can await them all, and every lease transition happens on the UI thread.
public sealed partial class WorkContextViewModel : ReactiveObject {
    PullRequestContextViewModel? _pullRequests;
    public PullRequestContextViewModel? PullRequests {
        get => _pullRequests;
        internal set {
            _pullRequests = value;
            if (value is not null) {
                value.PropertyChanged += OnPullRequestChanged;
                _disposables.Add(Disposable.Create(() => value.PropertyChanged -= OnPullRequestChanged));
            }
            this.RaisePropertyChanged(nameof(PullRequests));
            this.RaisePropertyChanged(nameof(IsStale));
            RebuildLinks();
        }
    }
    public bool ShowsLegacyLinks => PullRequests is null;
    PullRequestRepository? _primaryRepository;
    public PullRequestRepository? PrimaryRepository {
        get => _primaryRepository;
        private set {
            var changed = value is not null && value != _primaryRepository;
            _primaryRepository = value;
            if (changed) PullRequests?.PrimaryRepositoryChanged();
        }
    }
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly IReadOnlyList<HarnessOption> DefaultHarnessOptions = HostedHarnessCatalog.Build(null);

    internal const string WaitingNote      = "Waiting for session…";
    internal const string LoadingNote      = "Loading…";
    internal const string NoWorkItemNote   = "No work item yet. Ask the agent to link this session to an issue or task.";
    internal const string NoRepositoryNote = "No repository on this session, so a work item cannot attach.";
    internal const string SignedOutNote    = "Sign in to see the work context.";
    internal const string NotInPlanNote    = "Work items are not in this workspace's plan.";
    internal const string UnreachableNote  = "Couldn't reach the server.";

    readonly bool _isRemote;
    readonly IWorkContextSource _source;
    readonly SingleFlightReader<string, WorkContextRead> _reader;
    readonly SingleFlightReader<(string? Path, string? Reported), string> _branchReader;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly Action<string>? _openWorkItem;
    readonly SessionRuns _runs;
    readonly BehaviorSubject<bool> _canOpenWorkItem = new(false);
    readonly CompositeDisposable _disposables = new();
    ITimer? _timer;
    bool _tornDown;
    AgentStatusDto? _dto;

    string _repository = "—";
    public string Repository { get => _repository; private set => this.RaiseAndSetIfChanged(ref _repository, value); }
    string? _repositoryPath;
    public string? RepositoryPath { get => _repositoryPath; private set => this.RaiseAndSetIfChanged(ref _repositoryPath, value); }
    string _worktree = "—";
    public string Worktree { get => _worktree; private set => this.RaiseAndSetIfChanged(ref _worktree, value); }
    string? _worktreePath;
    public string? WorktreePath { get => _worktreePath; private set => this.RaiseAndSetIfChanged(ref _worktreePath, value); }
    string _branch = "—";
    public string Branch { get => _branch; private set => this.RaiseAndSetIfChanged(ref _branch, value); }
    string _harness = "—";
    public string Harness { get => _harness; private set => this.RaiseAndSetIfChanged(ref _harness, value); }
    string _transport = "—";
    public string Transport { get => _transport; private set => this.RaiseAndSetIfChanged(ref _transport, value); }
    string _tokens = SessionFactText.Unknown;
    public string Tokens { get => _tokens; private set => this.RaiseAndSetIfChanged(ref _tokens, value); }
    string _cost = SessionFactText.Unknown;
    public string Cost { get => _cost; private set => this.RaiseAndSetIfChanged(ref _cost, value); }
    string _contextUse = SessionFactText.Unknown;
    public string ContextUse { get => _contextUse; private set => this.RaiseAndSetIfChanged(ref _contextUse, value); }
    string _sessionSharing = SessionFactText.Unknown;
    public string SessionSharing { get => _sessionSharing; private set => this.RaiseAndSetIfChanged(ref _sessionSharing, value); }
    string _agentSharing = SessionFactText.Unknown;
    public string AgentSharing { get => _agentSharing; private set => this.RaiseAndSetIfChanged(ref _agentSharing, value); }
    string _sessionIdText = "resolving…";
    public string SessionIdText {
        get => _sessionIdText;
        private set {
            this.RaiseAndSetIfChanged(ref _sessionIdText, value);
            this.RaisePropertyChanged(nameof(SessionIdDisplay));
            this.RaisePropertyChanged(nameof(CanCopySessionId));
        }
    }
    /// Head and tail of a long id, so a 32-hex session id stays one line in the 320px pane.
    public string SessionIdDisplay => MiddleTruncate(_sessionIdText);
    public bool CanCopySessionId => _sessionIdText.Length > 0 && _sessionIdText != "resolving…";

    /// The session's subagents, shared with the chat tab; a session-local fact like the ones
    /// under SESSION, so it renders in every pane phase.
    public IAvaloniaReadOnlyList<RunRow> Runs => _runs.Rows;
    public bool HasRuns => _runs.Rows.Count > 0;
    /// What the collapsed section lists.
    public IAvaloniaReadOnlyList<RunRow> RunningRuns => _runs.Running;
    public bool HasRunningRuns => _runs.RunningCount > 0;
    public string RunsHeader {
        get {
            var running = _runs.RunningCount;
            var total = _runs.Rows.Count;
            return running switch {
                0                     => $"{total}",
                var r when r == total => $"{r} running",
                var r                 => $"{r} of {total} running",
            };
        }
    }

    static readonly RunState[] RunCountOrder =
        [RunState.Running, RunState.Done, RunState.Failed, RunState.Stopped];

    /// What the collapsed header shows: one entry per state something is in, so the numbers
    /// add up to the list.
    public IReadOnlyList<RunCount> RunCounts =>
        [.. RunCountOrder.Select(state => new RunCount(state, _runs.Count(state))).Where(c => c.Count > 0)];

    /// A subagent row the user chose to read; the workspace opens its transcript in place of the chat.
    public event Action<RunRow>? RunOpened;

    public ReactiveCommand<RunRow, Unit> OpenRunCommand { get; }

    /// The plan the session works from. Server-derived, and read on its own lease so a slow plan
    /// read never holds the work item back.
    public PlanSectionViewModel Plan { get; }

    void RefreshRuns() {
        this.RaisePropertyChanged(nameof(HasRuns));
        this.RaisePropertyChanged(nameof(HasRunningRuns));
        this.RaisePropertyChanged(nameof(RunsHeader));
        this.RaisePropertyChanged(nameof(RunCounts));
    }

    internal static string MiddleTruncate(string value, int head = 8, int tail = 8) {
        if (value.Length <= head + tail + 1) return value;
        return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - tail));
    }

    WorkContextPhase _phase = WorkContextPhase.WaitingForSession;
    public WorkContextPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(ShowsSignIn));
            this.RaisePropertyChanged(nameof(ShowsRetry));
            RaiseRelated();
        }
    }

    public string PhaseNote => Phase switch {
        WorkContextPhase.WaitingForSession or WorkContextPhase.SessionUnknown => WaitingNote,
        WorkContextPhase.Loading     => LoadingNote,
        WorkContextPhase.NoWorkItem  => _dto?.RepoPath is null ? NoRepositoryNote : NoWorkItemNote,
        WorkContextPhase.SignedOut   => SignedOutNote,
        WorkContextPhase.NotInPlan   => NotInPlanNote,
        WorkContextPhase.Unreachable => UnreachableNote,
        _                            => "",
    };

    public bool IsReady     => Phase == WorkContextPhase.Ready;
    public bool ShowsSignIn => Phase == WorkContextPhase.SignedOut;
    public bool ShowsRetry  => Phase == WorkContextPhase.Unreachable;

    void OnPullRequestChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PullRequestContextViewModel.HasChoice)
            or nameof(PullRequestContextViewModel.HasNotice)
            or nameof(PullRequestContextViewModel.HasReaderNote)
            or nameof(PullRequestContextViewModel.HasPullRequest)
            or nameof(PullRequestContextViewModel.HasListed))
            RaiseRelated();
        if (e.PropertyName == nameof(PullRequestContextViewModel.HasStaleOverview))
            this.RaisePropertyChanged(nameof(IsStale));
    }

    string? _reportedBranch;
    // The daemon reports the branch the worktree was cut on; HEAD knows a switch made since.
    void ResolveBranch(bool refresh = false) {
        if (_isRemote) {
            Branch = string.IsNullOrWhiteSpace(_reportedBranch) ? "—" : _reportedBranch;
            return;
        }
        var state = (Path: WorktreePath, Reported: _reportedBranch);
        if (_branchReader.HasCurrent && _branchReader.CurrentState == state) {
            if (refresh) _branchReader.Refresh();
            return;
        }
        Branch = string.IsNullOrWhiteSpace(_reportedBranch) ? "—" : _reportedBranch;
        _branchReader.Switch(state);
    }

    bool _isStale;
    public bool IsStale {
        get => _isStale || _pullRequests?.HasStaleOverview == true;
        private set {
            if (_isStale == value) return;
            _isStale = value;
            this.RaisePropertyChanged(nameof(IsStale));
        }
    }
    bool _isReading;
    public bool IsReading {
        get => _isReading;
        private set {
            if (_isReading == value) return;
            this.RaiseAndSetIfChanged(ref _isReading, value);
            if (!value) IsRefreshing = false;
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }
    bool _isRefreshing;
    /// A refresh someone asked for, still reading; the 30s poll runs without it.
    public bool IsRefreshing {
        get => _isRefreshing;
        private set { if (_isRefreshing != value) { this.RaiseAndSetIfChanged(ref _isRefreshing, value); this.RaisePropertyChanged(nameof(RefreshTip)); } }
    }
    bool _hasSession;
    // Subject, not WhenAnyValue — same RxAppBuilder init trap as SessionRailViewModel.SelectedSessionKey.
    readonly BehaviorSubject<bool> _hasSessionChanges = new(false);
    public bool HasSession {
        get => _hasSession;
        private set {
            if (_hasSession == value) return;
            this.RaiseAndSetIfChanged(ref _hasSession, value);
            _hasSessionChanges.OnNext(value);
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }

    bool _hasAgent;
    /// Until the daemon reports the agent, every session fact and the requester are placeholders.
    public bool HasAgent { get => _hasAgent; private set => this.RaiseAndSetIfChanged(ref _hasAgent, value); }

    /// Tip on the header refresh control — bound with ShowOnDisabled so a greyed icon still explains itself.
    public string RefreshTip => HasSession
        ? IsRefreshing ? "Refreshing…" : "Reloads the work item, its pull requests and the plan"
        : "Waiting for the session ID";

    public static string RefreshShortcutCaption =>
        RefreshShortcut.FromTerminalLabel is { } fromTerminal
            ? $"Refresh this work · {RefreshShortcut.Label} or {fromTerminal}"
            : $"Refresh this work · {RefreshShortcut.Label}";

    /// The Artefacts tab's model, when this pane's workspace has one; the pane switches and
    /// refreshes its session beside the plan's.
    public ArtefactsTabViewModel? Artefacts { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    /// The item's own page in the web UI; enabled once a read has named the item.
    public ReactiveCommand<Unit, Unit> OpenWorkItemCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _reader.PendingReadForTesting;

    public WorkContextViewModel(
            IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener,
            SessionRuns runs, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null,
            Action<string>? openWorkItem = null, IPlanSource? plans = null, PlanActivity? planActivity = null,
            ArtefactsTabViewModel? artefacts = null, bool isRemote = false) {
        _isRemote = isRemote;
        _source = source;
        _branchReader = new SingleFlightReader<(string? Path, string? Reported), string>(
            (state, ct) => Task.Run(() => {
                var branch = GitRepository.CurrentBranch(state.Path) ?? state.Reported;
                return string.IsNullOrWhiteSpace(branch) ? "—" : branch;
            }, ct),
            _ => string.IsNullOrWhiteSpace(_reportedBranch) ? "—" : _reportedBranch,
            branch => Branch = branch,
            "checkout branch");
        _reader = new SingleFlightReader<string, WorkContextRead>(
            _source.ReadAsync,
            ex => WorkContextRead.Of(WorkContextReadKind.Unreachable, ex.Message),
            Apply,
            "work context",
            reading => IsReading = reading,
            cached: _source.Cached);
        Plan = new PlanSectionViewModel(plans, planActivity ?? new PlanActivity(), time);
        Artefacts = artefacts;
        _opener = opener;
        _time = time;
        _openWorkItem = openWorkItem;
        _runs = runs;
        _runs.Changed += RefreshRuns;
        InitializeProjections();
        OpenRunCommand = ReactiveCommand.Create<RunRow>(row => { if (row.CanOpen) RunOpened?.Invoke(row); });
        _disposables.Add(OpenRunCommand);
        _disposables.Add(_hasSessionChanges);
        _disposables.Add(_canOpenWorkItem);

        // The pane's one refresh: the linked pull requests reload with the work item. Enabled for
        // any known session id. A click while a read is in flight queues one follow-up
        // (RefreshPending) instead of disabling the control — a greyed icon looked broken and ate
        // the click with no feedback.
        RefreshCommand = ReactiveCommand.Create(
            () => {
                ResolveBranch(refresh: true);
                PullRequests?.Refresh();
                Plan.Refresh();
                Artefacts?.Refresh();
                if (!_reader.HasCurrent) return;
                IsRefreshing = true;
                _reader.Refresh();
            },
            _hasSessionChanges);
        _disposables.Add(RefreshCommand);
        OpenWorkItemCommand = ReactiveCommand.Create(
            () => { if (WorkContextIds.ValidWorkItemId(PrimaryId) is { } id) _openWorkItem?.Invoke(id); },
            _canOpenWorkItem);
        _disposables.Add(OpenWorkItemCommand);
        SignInCommand = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        _disposables.Add(SignInCommand);

        presence.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(OnDto).DisposeWith(_disposables);
        signInCompleted?
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnSignInCompleted())
            .DisposeWith(_disposables);
        _timer = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(OnTick), null, PollInterval, PollInterval);
    }

    void OnDto(AgentStatusDto? dto) {
        if (_tornDown || dto is null) return;
        _dto = dto;
        UpdateFacts(dto);
        HasAgent = true;
        this.RaisePropertyChanged(nameof(ShowsPeople));
        if (dto.SessionId is { Length: > 0 } id && !string.Equals(_reader.CurrentState, id, StringComparison.Ordinal))
            SwitchSession(id);
        else if (_isRemote && string.IsNullOrEmpty(dto.SessionId) && HasSession) {
            _reader.ClearCurrent();
            HasSession = false;
            IsReading = false;
            SessionIdText = "resolving…";
            PrimaryRepository = null;
            ClearServerProjections();
            ApplySessionFacts(null);
            IsStale = false;
            Phase = WorkContextPhase.WaitingForSession;
            Plan.SwitchSession(null);
            Artefacts?.SwitchSession(null, null);
            _runs.Clear();
        }
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    internal static string TransportLabel(string family) => family switch {
        "pty" => "PTY",
        "acp" => "ACP",
        _     => "chat",
    };

    void UpdateFacts(AgentStatusDto dto) {
        Repository = RepoLabel.Leaf(dto.RepoPath);
        RepositoryPath = dto.RepoPath;
        var checkout = _isRemote ? dto.WorktreePath : CheckoutLabel.CheckoutPathFor(dto);
        WorktreePath = checkout;
        Worktree = checkout is null
            ? "—"
            : CheckoutLabel.Format(checkout, dto.RepoPath ?? "") + (dto.WorkLocation == WorkLocationText.Borrowed ? " · borrowed" : "");
        _reportedBranch = dto.Branch;
        ResolveBranch();
        var vendorLabel = HostedHarnessCatalog.LabelFor(DefaultHarnessOptions, dto.Vendor);
        Harness = $"{vendorLabel} · {HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "")}";
        Transport = TransportLabel(HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor));
        if (!_reader.HasCurrent) SessionIdText = SessionIds.Canonical(dto.SessionId) ?? "resolving…";
        UpdateRequester(dto, vendorLabel);
    }

    void SwitchSession(string id) {
        HasSession = true;
        SessionIdText = SessionIds.Canonical(id) ?? id;
        PrimaryRepository = null;
        ClearServerProjections();
        ApplySessionFacts(null);
        IsStale = false;
        Phase = WorkContextPhase.Loading;
        _reader.Switch(id);
        Plan.SwitchSession(id);
        Artefacts?.SwitchSession(id, _isRemote ? null : WorktreePath ?? RepositoryPath);
    }

    void OnTick() {
        if (_tornDown) return;
        if (_dto is not null) ResolveBranch(refresh: true);
        Plan.Refresh();
        if (Artefacts is { IsShown: true } or { LastReadFailed: true }) Artefacts.Refresh();
        _reader.StartIfIdle();
    }

    internal void Reconnected() {
        if (_tornDown) return;
        ForgetViewer();
        Plan.Refresh();
        Artefacts?.Refresh();
        _reader.Refresh();
    }

    void OnSignInCompleted() {
        if (_tornDown) return;
        ForgetViewer();
        Plan.Refresh();
        Artefacts?.Refresh();
        _reader.Refresh();
    }

    /// Applies one read for the current lease. Section-level merging lives in the projections half.
    void Apply(WorkContextRead read) {
        switch (read.Kind) {
            case WorkContextReadKind.SignedOut:      ApplyTerminal(WorkContextPhase.SignedOut);      return;
            case WorkContextReadKind.NotInPlan:
                // The plan gates work items only; a failed summary keeps the last session facts, stale.
                ApplyTerminal(WorkContextPhase.NotInPlan, keepSessionFacts: true);
                if (!read.SummaryFailed) ApplySessionFacts(read.Summary);
                else IsStale = _hasSessionFacts;
                return;
            case WorkContextReadKind.SessionUnknown: ApplyTerminal(WorkContextPhase.SessionUnknown); return;
            case WorkContextReadKind.Unreachable:
                // A plan-gated pane that holds session facts goes stale rather than blank.
                if (Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem
                    || (Phase == WorkContextPhase.NotInPlan && _hasSessionFacts)) IsStale = true;
                else Phase = WorkContextPhase.Unreachable;
                return;
            case WorkContextReadKind.Ready:
                ApplyReady(read);
                return;
            default:
                Phase = WorkContextPhase.Unreachable;
                return;
        }
    }

    /// The server has just said the viewer may not have this data, so nothing it gates stays visible.
    void ApplyTerminal(WorkContextPhase phase, bool keepSessionFacts = false) {
        ClearServerProjections();
        if (!keepSessionFacts) ApplySessionFacts(null);
        ForgetViewer();
        Phase = phase;
        IsStale = false;
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _runs.Changed -= RefreshRuns;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        await _reader.TeardownAsync();
        await _branchReader.TeardownAsync();
        await Plan.TeardownAsync();
    }
}
