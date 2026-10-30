using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One session row of the rail, retaining commands and bindings across directory revisions.
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public const string TerminalTooltip = "Started in a terminal, not hosted by a daemon";

    public string Id { get; }
    public string Key { get; }
    DateTime _createdAt;
    public DateTime CreatedAt { get => _createdAt; private set => this.RaiseAndSetIfChanged(ref _createdAt, value); }
    /// The server's last activity for a listed session; null for a row only a daemon knows.
    DateTime? _lastActivityAt;
    public DateTime? LastActivityAt { get => _lastActivityAt; private set => this.RaiseAndSetIfChanged(ref _lastActivityAt, value); }
    string? _checkoutPath;
    public string? CheckoutPath { get => _checkoutPath; private set => this.RaiseAndSetIfChanged(ref _checkoutPath, value); }
    /// Null when the row has no title, in which case the chips line stands alone as the row.
    string? _primary;
    public string? Primary { get => _primary; private set => this.RaiseAndSetIfChanged(ref _primary, value); }
    bool _hasTitle;
    public bool HasTitle { get => _hasTitle; private set => this.RaiseAndSetIfChanged(ref _hasTitle, value); }
    public string SessionAccessibleName => Primary ?? (HasVendor ? Vendor : "Session");
    string _vendor = "";
    public string Vendor { get => _vendor; private set => this.RaiseAndSetIfChanged(ref _vendor, value); }
    bool _hasVendor;
    public bool HasVendor { get => _hasVendor; private set => this.RaiseAndSetIfChanged(ref _hasVendor, value); }
    string? _model;
    public string? Model { get => _model; private set => this.RaiseAndSetIfChanged(ref _model, value); }
    string _meta = "";
    public string Meta { get => _meta; private set => this.RaiseAndSetIfChanged(ref _meta, value); }
    public string Tooltip => MachineBadge is { Length: > 0 } machine
        ? $"{Status.Tip}\n\n{machine}\nMachine · remote daemon" : Status.Tip;
    /// The daemon name badge for a remote row; null for a local one.
    string? _machineBadge;
    public string? MachineBadge { get => _machineBadge; private set => this.RaiseAndSetIfChanged(ref _machineBadge, value); }
    public string MachineLabel => $"on {MachineBadge}";
    public string MachineTooltip => $"This session runs on the remote daemon ‘{MachineBadge}’.";
    bool _isRemote;
    public bool IsRemote { get => _isRemote; private set => this.RaiseAndSetIfChanged(ref _isRemote, value); }
    /// A session the server lists with no daemon row behind it.
    bool _isServer;
    public bool IsServer { get => _isServer; private set => this.RaiseAndSetIfChanged(ref _isServer, value); }
    /// A live server row no daemon hosts: started in a terminal.
    bool _isTerminalSession;
    public bool IsTerminalSession { get => _isTerminalSession; private set => this.RaiseAndSetIfChanged(ref _isTerminalSession, value); }
    bool _isEnded;
    public bool IsEnded { get => _isEnded; private set => this.RaiseAndSetIfChanged(ref _isEnded, value); }
    /// Up to two initials of the owner's name, for a row that is not the signed-in user's.
    string? _ownerInitials;
    public string? OwnerInitials { get => _ownerInitials; private set => this.RaiseAndSetIfChanged(ref _ownerInitials, value); }
    string? _ownerAvatarUrl;
    public string? OwnerAvatarUrl { get => _ownerAvatarUrl; private set => this.RaiseAndSetIfChanged(ref _ownerAvatarUrl, value); }
    bool _showsOwner;
    public bool ShowsOwner { get => _showsOwner; private set => this.RaiseAndSetIfChanged(ref _showsOwner, value); }
    string _ownerTooltip = "";
    public string OwnerTooltip { get => _ownerTooltip; private set => this.RaiseAndSetIfChanged(ref _ownerTooltip, value); }
    string _repoLabel = "";
    public string RepoLabel { get => _repoLabel; private set => this.RaiseAndSetIfChanged(ref _repoLabel, value); }
    readonly ObservableAsPropertyHelper<bool> _showsRepo;
    /// The repository name moves onto the row when the groups no longer say it.
    public bool ShowsRepo => _showsRepo.Value;
    /// A launch the daemon has not published yet: Meta carries its stage instead of an age.
    bool _isStarting;
    public bool IsStarting { get => _isStarting; private set => this.RaiseAndSetIfChanged(ref _isStarting, value); }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPullRequestCommand { get; }
    public ReactiveCommand<RailPullRequestMenuItem, Unit> OpenPullRequestChoiceCommand { get; }
    RailPullRequestSummary? _pullRequest;
    public RailPullRequestSummary? PullRequest => _pullRequest;
    public string PullRequestLabel => PullRequest?.Label ?? "";
    public PullRequestStatus? PullRequestStatus => PullRequest?.Status;
    public bool HasPullRequestLifecycle => PullRequest?.HasLifecycle == true;
    public bool HasMultiplePullRequests => PullRequest?.Count > 1;
    public string PullRequestTooltip => $"{PullRequest?.Description}\n\nLinked to this session: {SessionAccessibleName}\n{(HasMultiplePullRequests ? "Choose a pull request" : "Open pull request details")}";
    public bool HasPullRequest => PullRequest is not null;
    public string PullRequestAccessibleName => $"{(HasMultiplePullRequests ? "Choose from" : "Open")} {PullRequestLabel}{(HasPullRequestLifecycle ? $", {PullRequest?.Lifecycle}" : "")} for {Primary ?? Vendor}";
    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly ObservableAsPropertyHelper<AgentStatusPresentation> _status;
    public AgentStatusPresentation Status => _status.Value;

    readonly ObservableAsPropertyHelper<bool> _isStale;
    /// A remote or server row greys out while the lane is stale, a server row also on the server's
    /// own idle flag; a local row is never stale.
    public bool IsStale => _isStale.Value;

    readonly CompositeDisposable _disposables = new();
    readonly BehaviorSubject<AgentRow> _row;
    readonly TimeProvider _time;

    public RailSessionViewModel(
            AgentRow row, IObservable<string?> selectedSessionKey,
            IObservable<IReadOnlySet<string>> agentsWithPending, IObservable<bool> remoteStale,
            Action<string> openLocal, Action<string> openRemote, TimeProvider time,
            IObservable<IReadOnlySet<string>>? agentsAwaitingAnswer = null,
            IObservable<IReadOnlyDictionary<string, int>>? agentsRunningCommands = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequests = null,
            IObservable<bool>? localOnAppServer = null, Action<string, AgentOrigin, PullRequestSubjectDto?>? openPullRequest = null,
            IObservable<Unit>? ageTicks = null,
            Action<string>? openServer = null, IObservable<RailGrouping>? grouping = null) {
        _time = time;
        _row = new BehaviorSubject<AgentRow>(row).DisposeWith(_disposables);
        Id = row.Id;
        Key = row.Key;
        UpdateFields(row);
        ageTicks?.Subscribe(_ => RefreshAge()).DisposeWith(_disposables);
        (pullRequests ?? Observable.Return<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                FrozenDictionary<string, RailPullRequestSummary>.Empty))
            .CombineLatest(localOnAppServer ?? Observable.Return(true), _row, (map, localReadable, current) =>
                current.SessionId is { Length: > 0 } sid && (current.Origin != AgentOrigin.Local || localReadable)
                    ? map.GetValueOrDefault(sid) : null)
            .DistinctUntilChanged()
            .Subscribe(summary => {
                this.RaiseAndSetIfChanged(ref _pullRequest, summary, nameof(PullRequest));
                this.RaisePropertyChanged(nameof(PullRequestLabel));
                this.RaisePropertyChanged(nameof(PullRequestStatus));
                this.RaisePropertyChanged(nameof(HasPullRequestLifecycle));
                this.RaisePropertyChanged(nameof(HasMultiplePullRequests));
                this.RaisePropertyChanged(nameof(PullRequestTooltip));
                this.RaisePropertyChanged(nameof(HasPullRequest));
                this.RaisePropertyChanged(nameof(PullRequestAccessibleName));
            }).DisposeWith(_disposables);
        // Pending launches open a local workspace; publication must keep that row selected.
        var selectionKey = row.Origin == AgentOrigin.Pending ? $"local:{row.Id}" : row.Key;
        _isSelected = selectedSessionKey.Select(sel => sel == selectionKey)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);

        var answering = agentsAwaitingAnswer ?? Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty);
        var commands = agentsRunningCommands ?? Observable.Return<IReadOnlyDictionary<string, int>>(FrozenDictionary<string, int>.Empty);
        _status = _row.CombineLatest(agentsWithPending, answering, commands,
                (current, pending, asked, running) => (Row: current, Pending: pending.Contains(current.Id), Asked: asked.Contains(current.Id), Running: running.GetValueOrDefault(current.Key)))
            .DistinctUntilChanged()
            .Select(state => SessionStatusDots.ForRow(state.Row, state.Pending, state.Asked, state.Running))
            .ToProperty(this, x => x.Status, initialValue: SessionStatusDots.ForRow(row, pending: false))
            .DisposeWith(_disposables);

        _isStale = _row.CombineLatest(remoteStale, (current, laneStale) =>
                current.Origin is AgentOrigin.Remote or AgentOrigin.Server && (laneStale || current.IsStale))
            .ToProperty(this, x => x.IsStale, initialValue: false)
            .DisposeWith(_disposables);

        _showsRepo = (grouping ?? Observable.Return(RailGrouping.Repository)).CombineLatest(_row, (mode, current) =>
                mode != RailGrouping.Repository && current.RepoGroupLabel is { Length: > 0 } and not "No repository")
            .ToProperty(this, x => x.ShowsRepo, initialValue: false)
            .DisposeWith(_disposables);

        OpenCommand = ReactiveCommand.Create(() => {
            if (IsServer) openServer?.Invoke(row.Id);
            else (IsRemote ? openRemote : openLocal)(row.Id);
        });
        _disposables.Add(OpenCommand);
        OpenPullRequestCommand = ReactiveCommand.Create(() => {
            if (HasPullRequest && !HasMultiplePullRequests)
                openPullRequest?.Invoke(Id, Origin(), PullRequest!.Subject);
        });
        _disposables.Add(OpenPullRequestCommand);
        OpenPullRequestChoiceCommand = ReactiveCommand.Create<RailPullRequestMenuItem>(item => {
            if (PullRequest?.MenuItems.Any(current => current.Subject == item.Subject) == true)
                openPullRequest?.Invoke(Id, Origin(), item.Subject);
        });
        _disposables.Add(OpenPullRequestChoiceCommand);
    }

    AgentOrigin Origin() => IsServer ? AgentOrigin.Server : IsRemote ? AgentOrigin.Remote : AgentOrigin.Local;

    internal void Update(AgentRow row) {
        UpdateFields(row);
        _row.OnNext(row);
    }

    void UpdateFields(AgentRow row) {
        var oldPrimary = Primary;
        var oldVendor = Vendor;
        var oldMachine = MachineBadge;
        CreatedAt = row.CreatedAt;
        LastActivityAt = row.LastActivityAt;
        CheckoutPath = (row.WorkLocation == WorkLocationText.Borrowed ? row.BorrowedFrom : row.WorktreePath) ?? row.RepoPath;
        Primary = string.IsNullOrEmpty(row.Title) ? null : row.Title;
        HasTitle = Primary is not null;
        Vendor = row.Vendor;
        HasVendor = !string.IsNullOrEmpty(row.Vendor);
        Model = string.IsNullOrEmpty(row.Model) ? null : row.Model;
        IsStarting = row.Origin == AgentOrigin.Pending;
        IsServer = row.Origin == AgentOrigin.Server;
        IsTerminalSession = row.IsTerminalSession;
        IsEnded = row.IsEndedSession;
        OwnerInitials = Initials(row.OwnerName);
        var avatar = string.IsNullOrEmpty(row.OwnerAvatarUrl) ? null : row.OwnerAvatarUrl;
        ShowsOwner = (OwnerInitials is not null || avatar is not null) && row.OwnerIsViewer != true;
        // A hidden badge's avatar still fetches, so only a shown one gets the URL.
        OwnerAvatarUrl = ShowsOwner ? avatar : null;
        OwnerTooltip = row.OwnerName is { Length: > 0 } owner ? $"{owner}\nOwner" : "";
        RepoLabel = row.RepoGroupLabel;
        Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : MetaFor(row);
        MachineBadge = row.MachineBadge;
        IsRemote = row.Origin == AgentOrigin.Remote;

        if (oldPrimary != Primary || oldVendor != Vendor) {
            this.RaisePropertyChanged(nameof(SessionAccessibleName));
            this.RaisePropertyChanged(nameof(PullRequestTooltip));
            this.RaisePropertyChanged(nameof(PullRequestAccessibleName));
        }
        if (oldMachine != MachineBadge) {
            this.RaisePropertyChanged(nameof(MachineLabel));
            this.RaisePropertyChanged(nameof(MachineTooltip));
            this.RaisePropertyChanged(nameof(Tooltip));
        }
    }

    /// Ended: when it ended and how long it ran. Live: the age, with the kind, borrow and subagent
    /// notes a daemon row carries. A remote registry row's registration time is not a start, so it
    /// shows no age.
    string MetaFor(AgentRow row) {
        var now = _time.GetUtcNow();
        var created = AsUtc(row.CreatedAt);
        if (row.IsEndedSession && row.EndedAt is { } endedRaw) {
            var ended = AsUtc(endedRaw);
            return $"{RelativeTime.Ago(new DateTimeOffset(ended), now)} · {UptimeFormat.Format(ended - created)}";
        }
        var kind = row.Kind is "agent" or "session" ? null : row.Kind;
        var borrowed = row.WorkLocation == WorkLocationText.Borrowed ? "borrowed" : null;
        var age = row.Origin == AgentOrigin.Remote ? null : LiveAge(now.UtcDateTime - created);
        var subagents = row.LiveSubagents is int live and > 0 ? $"{live} subagent{(live == 1 ? "" : "s")}" : null;
        return Join(kind, borrowed, age, subagents);
    }

    /// The age ticks once a minute, so a count of seconds would sit still beside the status word
    /// and read as how long the agent has been working, which the chat counts per turn.
    internal static string LiveAge(TimeSpan age) => age < TimeSpan.FromMinutes(1) ? "<1m" : UptimeFormat.Format(age);

    /// A local stamp is converted rather than relabelled: relabelling shifts it by the UTC offset,
    /// enough to move an age across a day boundary.
    static DateTime AsUtc(DateTime at) =>
        at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);

    void RefreshAge() {
        var row = _row.Value;
        if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Server)) return;
        Meta = MetaFor(row);
    }

    internal static string? Initials(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
