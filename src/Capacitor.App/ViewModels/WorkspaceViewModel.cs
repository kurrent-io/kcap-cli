using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.Resume;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum WorkspaceTab { Chat, Terminal, PullRequest, Artefacts }

/// Owns the persistent Chat, Terminal and PR surfaces for one agent. Presence is
/// replayed as accumulated state so each subscriber receives an already-cached agent.
/// Ended or removed agents retain their last known session context.
public sealed class WorkspaceViewModel : ReactiveObject, ISessionWorkspace {
    const string UnresolvedKind = "unresolved";

    public string AgentId { get; }

    readonly ObservableAsPropertyHelper<string> _title;
    public string Title => _title.Value;

    public SessionTitleEditorViewModel TitleEditor { get; }

    readonly ObservableAsPropertyHelper<string> _repoLabelText;
    /// Checkout under the title (`repo / worktree`); harness/transport live in the work-context pane.
    public string RepoLabelText => _repoLabelText.Value;

    readonly ObservableAsPropertyHelper<bool> _showsTerminalTab;
    public bool ShowsTerminalTab => _showsTerminalTab.Value;

    readonly ObservableAsPropertyHelper<bool> _sessionEnded;
    public bool SessionEnded => _sessionEnded.Value;

    readonly ObservableAsPropertyHelper<string?> _sessionId;
    /// The session the daemon reports for this agent, which a finished agent's workspace hands off by.
    public string? SessionId => _sessionId.Value;

    readonly ObservableAsPropertyHelper<string?> _lastStatus;
    /// The daemon's latest status word for this agent; Completed and Failed end differently.
    public string? LastStatus => _lastStatus.Value;

    readonly ObservableAsPropertyHelper<bool> _isStarting;
    /// A launch the daemon has not published yet; the header and the starting panel read the
    /// directory's pending row until the first dto lands.
    public bool IsStarting => _isStarting.Value;

    readonly ObservableAsPropertyHelper<bool> _hasAgent;
    /// The header's actions address an agent the daemon reports: before that the web page does not exist.
    public bool HasAgent => _hasAgent.Value;

    readonly ObservableAsPropertyHelper<bool> _showsStop;
    /// Stop reaches a starting launch too: the daemon cancels it before its agent registers.
    public bool ShowsStop => _showsStop.Value;

    readonly ObservableAsPropertyHelper<string> _startingText;
    public string StartingText => _startingText.Value;

    public TerminalTabViewModel Terminal { get; }

    public IObservable<Unit> Ended { get; }

    ChatTabViewModel? _chat;
    /// Built once, on the first dto; the reader comes from transcript_format and the input channel
    /// from has_terminal.
    SubagentPaneViewModel? _subagent;
    /// A subagent's own transcript, shown in place of the chat until the user goes back.
    public SubagentPaneViewModel? Subagent {
        get => _subagent;
        private set {
            this.RaiseAndSetIfChanged(ref _subagent, value);
            this.RaisePropertyChanged(nameof(ShowsChat));
            this.RaisePropertyChanged(nameof(ShowsSubagent));
        }
    }

    public ChatTabViewModel? Chat {
        get => _chat;
        private set {
            this.RaiseAndSetIfChanged(ref _chat, value);
            this.RaisePropertyChanged(nameof(ShowsChat));
        }
    }

    /// The right pane. Fed by the same presence stream as the header, so the daemon cache has one
    /// subscription per workspace, not two.
    public WorkContextViewModel WorkContext { get; }
    public PullRequestContextViewModel? PullRequests { get; }
    public ArtefactsTabViewModel Artefacts { get; }
    bool _showsArtefactsTab;
    public bool ShowsArtefactsTab {
        get => _showsArtefactsTab;
        private set {
            this.RaiseAndSetIfChanged(ref _showsArtefactsTab, value);
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        }
    }
    bool _showsPullRequestTab;
    public bool ShowsPullRequestTab {
        get => _showsPullRequestTab || IsPullRequestActive;
        private set {
            this.RaiseAndSetIfChanged(ref _showsPullRequestTab, value);
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
        }
    }
    public bool ShowsSurfaceSwitch => ShowsTerminalTab || ShowsPullRequestTab || ShowsArtefactsTab;

    WorkspaceTab _activeTab = WorkspaceTab.Chat;
    public WorkspaceTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            this.RaisePropertyChanged(nameof(IsChatActive));
            this.RaisePropertyChanged(nameof(ShowsChat));
            this.RaisePropertyChanged(nameof(ShowsSubagent));
            this.RaisePropertyChanged(nameof(IsTerminalActive));
            this.RaisePropertyChanged(nameof(IsPullRequestActive));
            this.RaisePropertyChanged(nameof(ShowsPullRequestTab));
            this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch));
            this.RaisePropertyChanged(nameof(ShowsTerminalBanners));
            this.RaisePropertyChanged(nameof(IsArtefactsActive));
            Artefacts.IsShown = IsArtefactsActive;
            PullRequests?.SetReaderVisible(value == WorkspaceTab.PullRequest);
            Terminal.SurfaceShown = IsTerminalActive;
        }
    }
    public bool IsChatActive => ActiveTab == WorkspaceTab.Chat;
    /// A chat surface with no view model behind it fails every visibility binding open, drawing
    /// its banners and composer as empty shells.
    public bool ShowsChat => IsChatActive && Chat is not null && Subagent is null;
    public bool ShowsSubagent => IsChatActive && Subagent is not null;
    public bool IsTerminalActive => ActiveTab == WorkspaceTab.Terminal;
    public bool IsPullRequestActive => ActiveTab == WorkspaceTab.PullRequest;
    public bool IsArtefactsActive => ActiveTab == WorkspaceTab.Artefacts;
    public bool ShowsTerminalBanners => IsTerminalActive;

    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPullRequestCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowArtefactsCommand { get; }

    public void OpenPullRequest(PullRequestSubjectDto? subject = null) {
        if (PullRequests is null) return;
        if (subject is not null) PullRequests.SelectSubject(subject);
        ActiveTab = WorkspaceTab.PullRequest;
        if (!PullRequests.IsReading) PullRequests.Refresh();
    }

    void ShowArtefacts() { if (ShowsArtefactsTab) ActiveTab = WorkspaceTab.Artefacts; }

    void PrefillComposer(string prompt) {
        if (Chat is not { } chat) return;
        chat.ComposerText = PullRequestContextViewModel.WithFixPrompt(chat.ComposerText, prompt);
        // The prompt is in the session's own composer, which an open subagent pane would hide.
        CloseSubagent();
        ActiveTab = WorkspaceTab.Chat;
    }

    /// A chat card's Open: a declared document selects in the tab, a page goes to the browser.
    internal void OpenCard(ToolCard card) {
        if (card.DocumentPath is { } path) {
            if (Artefacts.OpenDocument(path)) return;
            if (Artefacts.HasAny) { Artefacts.RequestOpen(); return; }
        }
        if (card.Url is { } url) LinkPolicy.Open(_opener, url);
    }

    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _lease = new();
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly IPermissionService _permissions;
    readonly SessionRuns _runs;

    // Read by StopCommand at click time -- the DTO's own Kind decides protected-ness
    // (AgentActionService.IsProtectedKind), so Stop must see whatever the LATEST resolved dto
    // says, not a value captured once at construction.
    AgentStatusDto? _latestDto;
    AgentRow? _startingRow;
    string? _appSubject;

    public WorkspaceViewModel(
            string agentId, IDaemonClientService daemon, AgentActionService actions,
            TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time,
            IUrlOpener opener, IPermissionService permissions, IWorkContextSource workContext, ILocalControlOps ops,
            IAttachmentUploader uploader, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null, IPullRequestSource? pullRequests = null, Action? linkGitHub = null,
            SessionAccessService? access = null, IObservable<bool>? localDaemonOnAppServer = null, IAgentDirectory? directory = null,
            IPlanSource? plans = null, IPlanArtifactSource? planArtifacts = null, BackgroundCommandActivity? commands = null,
            ComposerDrafts? drafts = null, Func<string, Action>? inputSending = null,
            Func<string, ResumeLedgerEntry?>? resumeLedger = null, LastReads<PullRequestSnapshot>? pullRequestSnapshots = null,
            LocalTranscripts? transcripts = null, Action<string>? pullRequestLinksChanged = null, AccountReadings? readings = null,
            IMessageSenderSource? senders = null,
            IObservable<string?>? appSubject = null, IAppNotifier? notifier = null,
            IObservable<ServerLaneStatus>? serverLane = null, WorkContextLayout? workContextLayout = null) {
        AgentId = agentId;
        _drafts = drafts;
        _opener = opener;
        _time = time;
        _permissions = permissions;
        Terminal = new TerminalTabViewModel(agentId, daemon, factory, surfaceFactory, time, resumeLedger) { SurfaceShown = IsTerminalActive };
        _disposables.Add(_lease);

        var presence = daemon.Agents.Connect()
            .Filter(dto => dto.Id == agentId)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Scan(new AgentPresence(null, false), Accumulate)
            .Replay(1)
            .RefCount();
        // A terminal status, not a removed row: a row dropped while its twin takes over on the
        // server lane is still a live session.
        Ended = presence.Where(p => p.Dto is not null)
            .Select(p => SessionStatusDots.IsTerminal(p.Dto!.Status))
            .DistinctUntilChanged()
            .SkipWhile(terminal => terminal)
            .Where(terminal => terminal)
            .Take(1)
            .Select(_ => Unit.Default);

        var runs = _runs = new SessionRuns(time);
        commands?.Track($"local:{agentId}", runs, () => Chat?.ReleaseFeed()).DisposeWith(_disposables);
        var planActivity = new PlanActivity();
        Artefacts = new ArtefactsTabViewModel(planArtifacts, planActivity, time, opener: opener);
        Artefacts.OpenRequested += ShowArtefacts;
        _disposables.Add(Disposable.Create(() => Artefacts.OpenRequested -= ShowArtefacts));
        // Keep the tab while a visible session's read is in flight; do not force it on for a
        // session that has never shown documents (that would flash the strip then hide it).
        Artefacts.WhenAnyValue(a => a.HasAny, a => a.IsLoading, (has, loading) => has || (loading && ShowsArtefactsTab))
            .Subscribe(show => {
                ShowsArtefactsTab = show;
                if (!show && IsArtefactsActive) ActiveTab = WorkspaceTab.Chat;
            }).DisposeWith(_disposables);
        WorkContext = new WorkContextViewModel(presence.Select(p => p.Dto), workContext, time, opener, runs, requestSignIn, signInCompleted,
            actions.OpenWorkItemInWeb, plans, planActivity, artefacts: Artefacts, serverLane: serverLane, layout: workContextLayout);
        WorkContext.RunOpened += OpenSubagent;
        _disposables.Add(Disposable.Create(() => WorkContext.RunOpened -= OpenSubagent));
        // An open subagent belongs to the transcript it was opened from.
        presence.Select(p => p.Dto?.TranscriptPath).SkipWhile(path => path is null).DistinctUntilChanged().Skip(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => CloseSubagent()).DisposeWith(_disposables);
        PullRequests = pullRequests is null ? null : new PullRequestContextViewModel(presence.Select(p => p.Dto), pullRequests, time, opener,
            () => ActiveTab = WorkspaceTab.PullRequest, requestSignIn, linkGitHub, signInCompleted, () => WorkContext.PrimaryRepository,
            snapshots: pullRequestSnapshots, linksChanged: pullRequestLinksChanged, requestFix: PrefillComposer, canRequestFix: () => Chat is { ShowsComposer: true });
        WorkContext.PullRequests = PullRequests;
        if (PullRequests is { } context)
            context.HasPullRequestChanges.CombineLatest(context.WhenAnyValueUnsafe(pr => pr.HasListed), (has, listed) => (has, listed))
                .Subscribe(state => {
                    ShowsPullRequestTab = state.has;
                    if (!state.has && IsPullRequestActive && state.listed) ActiveTab = WorkspaceTab.Chat;
                }).DisposeWith(_disposables);
        daemon.Status.Select(status => status.State).DistinctUntilChanged().Skip(1)
            .Where(state => state == AttachState.Connected).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => PullRequests?.Reconnected()).DisposeWith(_disposables);
        // The pull requests read the app's server, so its coming back is what clears a failed discovery.
        serverLane?.Select(s => s.State).DistinctUntilChanged().Skip(1)
            .Where(state => state == ServerLaneState.Connected).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => PullRequests?.Reconnected()).DisposeWith(_disposables);

        presence.Select(p => p.Dto).Subscribe(dto => {
            _latestDto = dto;
            // Only Claude Code files a subagent's transcript beside its parent's, so only its rows open here.
            runs.OpensSubagents = dto is not null && FilesSubagentsBesideItself(dto);
        }).DisposeWith(_disposables);

        // Replays through presence, which the cards pipeline relies on: its filter admits nothing
        // until a first session id arrives.
        var sessionIds = presence.Select(p => p.Dto?.SessionId).DistinctUntilChanged();

        // An ACP-hosted agent's question reaches the app only over the server lane, in this
        // session's chat group, local agent or not — so a local workspace joins it too. A local
        // agent the server never registered answers Denied; nothing here reads the verdict, which
        // is what keeps that invisible. The join is scoped to the app's own server: on any other
        // server this id names a different session, and joining its group would deliver that
        // session's prompts into this pane.
        if (access is not null)
            sessionIds
                .CombineLatest(localDaemonOnAppServer ?? Observable.Return(true), (sessionId, onAppServer) => onAppServer ? sessionId : null)
                .DistinctUntilChanged()
                .Subscribe(sessionId => _lease.Disposable = sessionId is null ? Disposable.Empty : access.Acquire(sessionId))
                .DisposeWith(_disposables);

        // The daemon cache emits nothing for an agent it has not published, so the pending row
        // is combined with an explicit empty presence rather than waiting on the first dto.
        var pendingRow = directory is null
            ? Observable.Return<AgentRow?>(null)
            : directory.Rows.Connect()
                .Filter(row => row.Id == agentId && row.Origin == AgentOrigin.Pending)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .QueryWhenChanged(query => query.Items.FirstOrDefault())
                .StartWith((AgentRow?)null);
        var header = presence.StartWith(new AgentPresence(null, false))
            .CombineLatest(pendingRow, (p, row) => (p.Dto, Row: p.Dto is null ? row : null));

        var titles = header.Select(h => h.Dto is not null ? TitleFor(h.Dto) : h.Row?.Title ?? TitleFor(null));
        _title = titles
            .ToProperty(this, x => x.Title, TitleFor(null))
            .DisposeWith(_disposables);

        // The daemon renames as its own account, so it must be the app's account on the app's
        // server; a private agent makes no per-agent server calls at all, and an ended one has no
        // title loop left to hold the rename.
        var subject = (appSubject ?? Observable.Return<string?>(null)).DistinctUntilChanged().Replay(1).RefCount();
        subject.Subscribe(s => _appSubject = s).DisposeWith(_disposables);
        var canRename = Observable.CombineLatest(
                daemon.Status.Select(s => s.State == AttachState.Connected && s.Capabilities?.Contains(RenameWire.Capability) == true),
                localDaemonOnAppServer ?? Observable.Return(false),
                daemon.Snapshots.Select(s => s.Daemon.UserSubject).StartWith((string?)null),
                subject,
                presence.Select(p => !p.SessionEnded && p.Dto is { IsPrivate: false, SessionId.Length: > 0 }),
                (capable, onAppServer, daemonSubject, viewer, agent) =>
                    capable && onAppServer && agent && viewer is not null && daemonSubject == viewer)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        TitleEditor = new SessionTitleEditorViewModel(titles, canRename, async (title, ct) => {
            var ack = await ops.RenameSessionAsync(new SessionRenameDto(agentId, title, _appSubject), ct);
            return new SessionRenameResult(ack.Ok, ack.Title, ack.Ok ? null : ack.Reason);
        }, notifier ?? new AppNotifier()).DisposeWith(_disposables);
        _repoLabelText = header.Select(h => h.Dto is not null ? CheckoutLabelFor(h.Dto) : h.Row is { } row ? RepoLabel.Leaf(row.RepoPath) : CheckoutLabelFor(null))
            .ToProperty(this, x => x.RepoLabelText, CheckoutLabelFor(null))
            .DisposeWith(_disposables);
        _hasAgent = header.Select(h => h.Dto is not null)
            .ToProperty(this, x => x.HasAgent, initialValue: false)
            .DisposeWith(_disposables);
        _isStarting = header.Select(h => h.Row is not null)
            .ToProperty(this, x => x.IsStarting, initialValue: false)
            .DisposeWith(_disposables);
        _showsStop = header.Select(h => h.Dto is not null || h.Row is not null)
            .ToProperty(this, x => x.ShowsStop, initialValue: false)
            .DisposeWith(_disposables);
        header.Subscribe(h => _startingRow = h.Row).DisposeWith(_disposables);
        _startingText = header.Select(h => h.Row is { } row ? LaunchStages.StartingText(row.Vendor, row.LaunchStage) : "")
            .ToProperty(this, x => x.StartingText, initialValue: "")
            .DisposeWith(_disposables);
        var showsTerminal = presence.Select(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor));
        _showsTerminalTab = showsTerminal
            .ToProperty(this, x => x.ShowsTerminalTab, initialValue: false)
            .DisposeWith(_disposables);
        showsTerminal.Subscribe(_ => this.RaisePropertyChanged(nameof(ShowsSurfaceSwitch)))
            .DisposeWith(_disposables);
        // ShowTerminalCommand is unguarded — a caller can select the tab before any dto says whether
        // this agent has one — so presence clamps it back rather than leaving a blank pane in front.
        showsTerminal.Subscribe(shows => {
            if (!shows && IsTerminalActive) ActiveTab = WorkspaceTab.Chat;
        }).DisposeWith(_disposables);
        _sessionEnded = presence.Select(p => p.SessionEnded)
            .ToProperty(this, x => x.SessionEnded, initialValue: false)
            .DisposeWith(_disposables);
        _sessionId = presence.Select(p => p.Dto?.SessionId)
            .ToProperty(this, x => x.SessionId, initialValue: null)
            .DisposeWith(_disposables);
        _lastStatus = presence.Select(p => p.Dto?.Status)
            .ToProperty(this, x => x.LastStatus, initialValue: null)
            .DisposeWith(_disposables);

        presence
            .Where(p => p.Dto is not null && !LacksRuntimeMetadata(p.Dto))
            .Take(1)
            .Subscribe(p => {
                var dto = p.Dto!;
                var (projection, note) = ChatTranscriptSource.Resolve(dto);
                var hasTerminal = HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor);
                ChatInput input = hasTerminal
                    ? new TerminalChatInput(Terminal, agentId, daemon, ops, presence)
                    : new LocalFrameChatInput(agentId, daemon, ops, presence);
                Chat = new ChatTabViewModel(
                    agentId, daemon, input, uploader, projection, opener, time, permissions, runs, note, sessionIds, localDaemonOnAppServer, planActivity, OpenCard, drafts, transcripts,
                    senders, readings?.Local, readings?.Clock, signInCompleted) {
                    ShowTerminal = hasTerminal ? () => ActiveTab = WorkspaceTab.Terminal : null,
                    InputSending = inputSending,
                };
                if (PullRequests is { } fixHost)
                    Chat.WhenAnyValue(chat => chat.ShowsComposer).Subscribe(_ => fixHost.FixAvailabilityChanged()).DisposeWith(_disposables);
            })
            .DisposeWith(_disposables);

        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Terminal; });
        ShowPullRequestCommand = ReactiveCommand.Create(() => { if (ShowsPullRequestTab) ActiveTab = WorkspaceTab.PullRequest; });
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);
        _disposables.Add(ShowPullRequestCommand);
        ShowArtefactsCommand = ReactiveCommand.Create(ShowArtefacts);
        _disposables.Add(ShowArtefactsCommand);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWeb(agentId));
        _disposables.Add(OpenInWebCommand);

        var stopKey = AgentActionService.StopKey(AgentOrigin.Local, agentId);
        var canStop = presence.StartWith(new AgentPresence(null, false)).Select(p => !p.SessionEnded)
            .CombineLatest(actions.StopsInFlight, (alive, inFlight) => alive && !inFlight.Contains(stopKey));
        StopCommand = ReactiveCommand.Create(() => {
            var dto = _latestDto;
            // UnresolvedKind fails safe as protected (AgentActionService.IsProtectedKind treats
            // anything other than exactly "agent" as protected) -- the edge case of a Stop click
            // before the first dto ever arrives must never default to the ONE kind that skips
            // the confirm-then-force seam.
            var row   = _startingRow;
            var kind  = dto?.Kind ?? row?.Kind ?? UnresolvedKind;
            var label = dto is not null ? $"{dto.Kind} · {dto.Vendor} · {RepoLabel.Leaf(dto.RepoPath)}"
                      : row is not null ? $"{row.Kind} · {row.Vendor} · {RepoLabel.Leaf(row.RepoPath)}"
                      : agentId;
            actions.RequestStop(agentId, label, kind);
        }, canStop);
        _disposables.Add(StopCommand);
    }

    /// A resuming row the daemon has no runtime answers for; the reader and input chosen from it would be
    /// the older-daemon fallbacks, for good. A resume publishes the agent with them. An interrupted row
    /// is not held back: it may stay interrupted for its whole retention, and a fallback chat beats none.
    static bool LacksRuntimeMetadata(AgentStatusDto dto) =>
        dto.Status == "Resuming" && dto.HasTerminal is null && dto.TranscriptFormat is null;

    static AgentPresence Accumulate(AgentPresence state, IChangeSet<AgentStatusDto, string> changes) {
        var dto = state.Dto;
        var ended = state.SessionEnded;
        foreach (var change in changes) {
            if (change.Reason == ChangeReason.Remove) { ended = true; continue; } // dto stays frozen
            dto = change.Current;
            // The latest row decides, not the first end seen: a resume brings an ended agent back under its id.
            ended = SessionStatusDots.IsTerminal(dto.Status);
        }
        return new AgentPresence(dto, ended);
    }

    /// `repo / checkout`, with the marker for a borrowed reviewer; the repository alone from an
    /// older daemon.
    internal static string CheckoutLabelFor(AgentStatusDto? dto) {
        var repo = RepoLabel.Leaf(dto?.RepoPath);
        if (dto is null || CheckoutLabel.CheckoutPathFor(dto) is not { } checkout) return repo;

        var label = $"{repo} / {CheckoutLabel.Format(checkout, dto.RepoPath ?? "")}";

        return dto.WorkLocation == WorkLocationText.Borrowed ? $"{label} · borrowed" : label;
    }

    static string TitleFor(AgentStatusDto? dto) => dto?.Title ?? RepoLabel.Leaf(dto?.RepoPath);

    readonly ComposerDrafts? _drafts;

    public void HandDraftTo(AgentOrigin origin) => _drafts?.Move(AgentOrigin.Local, origin, AgentId);

    /// A subagent's transcript sits beside its parent's on this machine, written as it runs.
    void OpenSubagent(RunRow row) {
        // Only Claude Code files a subagent's transcript beside its parent's.
        if (row.AgentId is not { } subagentId || _latestDto is not { TranscriptPath: { } parentPath } dto || !FilesSubagentsBesideItself(dto)
            || TranscriptChat.ForSubagent(dto.Vendor) is not { } projection) return;
        var path   = ClaudeSubagentFiles.TranscriptPath(parentPath, subagentId);
        var logged = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        ReplaceSubagent(new SubagentPaneViewModel(
            _runs, row, Title, AgentOrigin.Local, AgentId, dto.Vendor, path,
            key => new LocalTranscriptFeed(key, projection, subagentId, _time,
                reason => { if (logged.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: subagent transcript: {reason}"); }),
            _opener, _time, _permissions, CloseSubagent));
        ActiveTab = WorkspaceTab.Chat;
    }

    /// Claude Code's own transcript, known and not a journal the daemon writes for it.
    static bool FilesSubagentsBesideItself(AgentStatusDto dto) =>
        dto is { Vendor: "claude", TranscriptPath: not null } && dto.TranscriptFormat != TranscriptFormats.Envelopes;

    void CloseSubagent() => ReplaceSubagent(null);

    void ReplaceSubagent(SubagentPaneViewModel? next) {
        var previous = Subagent;
        Subagent = next;
        if (previous is not null) _ = previous.TeardownAsync();
    }

    /// Disposes this workspace's own daemon-cache projections, then tears down Chat (if built), the
    /// work-context pane, and Terminal last -- the caller that closes a workspace tab calls this once.
    public async Task TeardownAsync() {
        _disposables.Dispose();
        if (PullRequests is { } pullRequests) await pullRequests.TeardownAsync();
        if (Chat is { } chat) await chat.TeardownAsync();
        if (Subagent is { } subagent) await subagent.TeardownAsync();
        await WorkContext.TeardownAsync();
        await Artefacts.TeardownAsync();
        await Terminal.TeardownAsync();
    }
}
