using System.Globalization;
using System.Reactive;
using Avalonia.Collections;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The server-derived half of the pane and how one read merges into it, section by section: a
/// failed section keeps its last projection and marks the pane stale, an authoritative empty
/// answer clears it, and a terminal phase clears everything the server gave.
public sealed partial class WorkContextViewModel {
    readonly AvaloniaList<WorkContextPartViewModel> _parts = new();
    readonly AvaloniaList<WorkContextPartViewModel> _thisSessionParts = new();
    IReadOnlyList<WorkContextPartCount> _partCounts = [];
    readonly AvaloniaList<string> _blockedBy = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _links = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _issues = new();
    readonly List<WorkContextLinkViewModel> _summaryPrs = [];
    readonly AvaloniaList<WorkContextPersonViewModel> _contributors = new();
    IReadOnlyList<WorkItemContributorDto> _contributorRows = [];
    // The card's identity is the id the server served, which for an absorbed item is the survivor's
    // rather than the assignment's; the requested id stands in when a read carried no item.
    string? _primaryId;
    string? _requestedId;
    string? PrimaryId {
        get => _primaryId;
        set {
            _primaryId = value;
            _canOpenWorkItem.OnNext(WorkContextIds.ValidWorkItemId(value) is not null && _openWorkItem is not null);
        }
    }

    public IAvaloniaReadOnlyList<WorkContextPartViewModel> Parts => _parts;
    /// What the collapsed section lists: the parts this session is attached to, in part order.
    public IAvaloniaReadOnlyList<WorkContextPartViewModel> ThisSessionParts => _thisSessionParts;
    /// Open, this session, done: one entry per mark some part carries, so the numbers add up to the list.
    public IReadOnlyList<WorkContextPartCount> PartCounts => _partCounts;
    public IAvaloniaReadOnlyList<string> BlockedBy => _blockedBy;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Links => _links;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Issues => _issues;
    public IAvaloniaReadOnlyList<WorkContextPersonViewModel> Contributors => _contributors;

    string? _key;
    public string? Key {
        get => _key;
        private set { this.RaiseAndSetIfChanged(ref _key, value); NotifyIdentity(); }
    }
    string _title = "";
    public string Title {
        get => _title;
        private set { this.RaiseAndSetIfChanged(ref _title, value); NotifyIdentity(); }
    }
    bool IsPrimaryIssue(WorkContextLinkViewModel? issue) =>
        issue is not null && string.Equals(issue.Key, Key, StringComparison.OrdinalIgnoreCase);
    static bool IsFallbackIssueTitle(WorkContextLinkViewModel issue) =>
        issue.Title.Length == 0 || string.Equals(issue.Title, issue.Key, StringComparison.OrdinalIgnoreCase)
        || string.Equals(issue.Title, $"Issue {issue.Key}", StringComparison.OrdinalIgnoreCase);
    WorkContextLinkViewModel? PrimaryIssue =>
        _issues.FirstOrDefault(IsPrimaryIssue) ?? _issues.FirstOrDefault();
    public string DisplayTitle => Title.Length > 0 && !string.Equals(Title, Key, StringComparison.OrdinalIgnoreCase) ? Title
        : PrimaryIssue is { } primary && IsPrimaryIssue(primary) && !IsFallbackIssueTitle(primary) ? primary.Title : "";
    public bool HasInlineIssue => PrimaryIssue is { } primary && IsPrimaryIssue(primary)
        && (IsFallbackIssueTitle(primary) || string.Equals(primary.Title, DisplayTitle, StringComparison.OrdinalIgnoreCase));
    /// Issues the section should show: every link-class issue except a primary absorbed into the work-item title.
    public IEnumerable<WorkContextLinkViewModel> SeparateIssues =>
        HasInlineIssue ? _issues.Where(i => !IsPrimaryIssue(i)) : _issues;
    /// The issue the section shows when there is only one separate entry — not the raw first
    /// server seed, which may be absorbed into the work-item title.
    public WorkContextLinkViewModel? SeparateIssue => SeparateIssues.FirstOrDefault();
    public bool HasSeparateIssue => SeparateIssue is not null;
    public bool HasMultipleIssues => SeparateIssues.Skip(1).Any();
    /// Issue titles wrap over several lines, so two already make a tall section.
    internal const int VisibleIssuesCap = 2;
    public bool IssuesOverflows => SeparateIssues.Skip(VisibleIssuesCap).Any();
    public IEnumerable<WorkContextLinkViewModel> VisibleSeparateIssues =>
        IssuesExpanded || !IssuesOverflows ? SeparateIssues : SeparateIssues.Take(VisibleIssuesCap);
    public string IssueSectionEyebrow => HasMultipleIssues ? "ISSUES" : "ISSUE";
    public string IssueSectionMeta => HasMultipleIssues
        ? SeparateIssues.Count().ToString(CultureInfo.InvariantCulture)
        : SeparateIssue?.Key ?? "";

    void NotifyIdentity() {
        this.RaisePropertyChanged(nameof(DisplayTitle));
        this.RaisePropertyChanged(nameof(HasInlineIssue));
        this.RaisePropertyChanged(nameof(HasSeparateIssue));
        this.RaisePropertyChanged(nameof(SeparateIssue));
        this.RaisePropertyChanged(nameof(HasMultipleIssues));
        this.RaisePropertyChanged(nameof(IssuesOverflows));
        this.RaisePropertyChanged(nameof(VisibleSeparateIssues));
        this.RaisePropertyChanged(nameof(IssueSectionEyebrow));
        this.RaisePropertyChanged(nameof(IssueSectionMeta));
        this.RaisePropertyChanged(nameof(SeparateIssues));
        RaiseRelated();
    }
    string? _overview;
    public string? Overview { get => _overview; private set => this.RaiseAndSetIfChanged(ref _overview, value); }
    string? _stateLabel;
    public string? StateLabel { get => _stateLabel; private set => this.RaiseAndSetIfChanged(ref _stateLabel, value); }
    bool _isShipped;
    public bool IsShipped { get => _isShipped; private set => this.RaiseAndSetIfChanged(ref _isShipped, value); }
    bool _isClosed;
    public bool IsClosed { get => _isClosed; private set => this.RaiseAndSetIfChanged(ref _isClosed, value); }
    string? _partOfTitle;
    public string? PartOfTitle { get => _partOfTitle; private set => this.RaiseAndSetIfChanged(ref _partOfTitle, value); }
    string? _cycleNote;
    public string? CycleNote { get => _cycleNote; private set => this.RaiseAndSetIfChanged(ref _cycleNote, value); }

    WorkContextLinkViewModel? _issue;
    public WorkContextLinkViewModel? Issue {
        get => _issue;
        private set {
            if (ReferenceEquals(_issue, value)) return;
            this.RaiseAndSetIfChanged(ref _issue, value);
            NotifyIdentity();
        }
    }

    int _sessionCount;
    int SessionCount {
        get => _sessionCount;
        set {
            if (_sessionCount == value) return;
            _sessionCount = value;
            this.RaisePropertyChanged(nameof(WhoCountText));
        }
    }

    public string PartsHeader => _parts.Count switch {
        0     => "0",
        var n => $"{SettledCount} of {n}",
    };
    int SettledCount => _parts.Count(p => p.IsSettled);
    public bool HasParts => _parts.Count > 0;
    public bool HasThisSessionParts => _thisSessionParts.Count > 0;
    public bool HasBlockers => _blockedBy.Count > 0;
    public bool HasTopologyNotes => HasBlockers || !string.IsNullOrEmpty(CycleNote);
    public bool HasContributors => _contributors.Count > 0;
    public bool ShowsPeople => HasAgent && (_viewer is null
        || (_contributorRows.Count > 0
            ? _contributorRows.Any(c => !_viewer.Owns(c.UserId))
            : _requesterId is not null && !_viewer.Owns(_requesterId)));
    /// The card carries the PR picker and the live checks and review rows, so it stays while it
    /// has a PR to select or something to say, including the empty-list message during discovery
    /// and polling. The pane's own empty copy is a fallback when there is no card content.
    /// Before a session id the card could only repeat the pane's waiting note.
    public bool ShowsPullRequestCard =>
        Phase != WorkContextPhase.WaitingForSession
        && PullRequests is { } prs && (prs.HasChoice || prs.HasNotice || prs.HasReaderNote);
    public bool ShowsLegacyLinkCards => ShowsLegacyLinks && _links.Count > 0;
    /// Empty copy waits until the session list and the work-item read have both settled — an
    /// earlier miss is often the item's link, not a missing PR.
    public bool ShowsPullRequestEmpty =>
        Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem
        && (PullRequests?.HasListed ?? true)
        && PullRequests is not { HasPullRequest: true }
        && _links.Count == 0
        && !ShowsPullRequestCard;
    public const string PullRequestEmptyNote = "No pull request linked";
    public bool ShowsPullRequestSection => ShowsPullRequestCard || ShowsLegacyLinkCards || ShowsPullRequestEmpty;
    public string WhoCountText => _sessionCount switch {
        0     => "",
        1     => "1 session",
        var n => $"{n} sessions",
    };

    string _requester = "You";
    public string Requester { get => _requester; private set => this.RaiseAndSetIfChanged(ref _requester, value); }
    string _requesterRole = "";
    public string RequesterRole { get => _requesterRole; private set => this.RaiseAndSetIfChanged(ref _requesterRole, value); }
    string _requesterInitial = "Y";
    public string RequesterInitial { get => _requesterInitial; private set => this.RaiseAndSetIfChanged(ref _requesterInitial, value); }
    string? _requesterId;
    ViewerIdentityDto? _viewer;
    public ViewerIdentityDto? ViewerIdentity {
        get => _viewer;
        private set {
            this.RaiseAndSetIfChanged(ref _viewer, value);
            RefreshContributors();
        }
    }
    string? _requesterAvatarUrl;
    public string? RequesterAvatarUrl { get => _requesterAvatarUrl; private set => this.RaiseAndSetIfChanged(ref _requesterAvatarUrl, value); }

    bool _partsExpanded;
    public bool PartsExpanded {
        get => _partsExpanded;
        private set {
            this.RaiseAndSetIfChanged(ref _partsExpanded, value);
            this.RaisePropertyChanged(nameof(PartsAccessibleName));
        }
    }
    /// The folded counts are told apart only by their marks, so the header names them in words.
    public string PartsAccessibleName => PartsExpanded || _partCounts.Count == 0
        ? $"Parts, {PartsHeader}"
        : $"Parts, {string.Join(", ", _partCounts.Select(c => c.Label))}";
    bool _peopleExpanded;
    public bool PeopleExpanded { get => _peopleExpanded; private set => this.RaiseAndSetIfChanged(ref _peopleExpanded, value); }
    bool _issuesExpanded;
    public bool IssuesExpanded {
        get => _issuesExpanded;
        private set {
            this.RaiseAndSetIfChanged(ref _issuesExpanded, value);
            this.RaisePropertyChanged(nameof(VisibleSeparateIssues));
        }
    }
    readonly WorkContextLayout _layout;
    /// Shared by every session's pane, so switching sessions keeps what the reader opened.
    public bool SessionExpanded {
        get => _layout.SessionExpanded;
        private set => _layout.SessionExpanded = value;
    }

    void OnLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(WorkContextLayout.SessionExpanded)) return;
        this.RaisePropertyChanged(nameof(SessionExpanded));
        this.RaisePropertyChanged(nameof(SessionToggleText));
    }
    public string SessionToggleText => SessionExpanded ? "Less" : "More";
    bool _runsExpanded;
    public bool RunsExpanded { get => _runsExpanded; private set => this.RaiseAndSetIfChanged(ref _runsExpanded, value); }

    public ReactiveCommand<Unit, Unit> TogglePartsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePeopleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleIssuesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSessionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRunsCommand { get; private set; } = null!;

    void InitializeProjections() {
        TogglePartsCommand   = Toggle(() => PartsExpanded = !PartsExpanded);
        TogglePeopleCommand  = Toggle(() => PeopleExpanded = !PeopleExpanded);
        ToggleIssuesCommand  = Toggle(() => {
            if (IssuesOverflows) IssuesExpanded = !IssuesExpanded;
            else if (!HasMultipleIssues && SeparateIssue is { CanOpen: true } issue)
                LinkPolicy.Open(_opener, issue.Url);
        });
        ToggleSessionCommand = Toggle(() => SessionExpanded = !SessionExpanded);
        _layout.PropertyChanged += OnLayoutChanged;
        _disposables.Add(System.Reactive.Disposables.Disposable.Create(() => _layout.PropertyChanged -= OnLayoutChanged));
        ToggleRunsCommand = Toggle(() => RunsExpanded = !RunsExpanded);
        WatchOwnWorktree();
    }

    ReactiveCommand<Unit, Unit> Toggle(Action flip) {
        var command = ReactiveCommand.Create(flip);
        _disposables.Add(command);
        return command;
    }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) {
        Requester = FirstNonBlank(dto.RequesterDisplay, dto.Requester) ?? "You";
        RequesterRole = $"This session · {vendorLabel}";
        RequesterInitial = WorkContextPersonViewModel.InitialOf(Requester);
        _requesterId = dto.Requester;
        ResolveRequesterAvatar();
        this.RaisePropertyChanged(nameof(ShowsPeople));
    }

    /// A launch with no requester is the daemon owner's, and the viewer only sees their own daemons.
    /// Anyone else gets a picture only from a `github:` id.
    void ResolveRequesterAvatar() =>
        RequesterAvatarUrl = _viewer is { AvatarUrl: { } own } && (_requesterId is null || _viewer.Owns(_requesterId))
            ? own
            : GitHubAvatarUrl(_requesterId);

    void ForgetViewer() {
        ViewerIdentity = null;
        ResolveRequesterAvatar();
    }

    static string? GitHubAvatarUrl(string? userId) =>
        userId is not null && userId.StartsWith("github:", StringComparison.Ordinal)
        && long.TryParse(userId.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? $"https://avatars.githubusercontent.com/u/{id}?v=4&s=64" : null;

    static string? FirstNonBlank(params string?[] values) {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    void ClearServerProjections() {
        ClearCard();
        _summaryPrs.Clear();
        _links.Clear();
        RaiseRelated();
    }

    void ClearCard() {
        PrimaryId = null;
        _requestedId = null;
        ClearItem();
        ClearTopology();
    }

    void ClearItem() {
        Key = null;
        Title = "";
        Overview = null;
        ApplyState(null);
        _parts.Clear();
        _issues.Clear();
        IssuesExpanded = false;
        Issue = null;
        _contributors.Clear();
        _contributorRows = [];
        SessionCount = 0;
        RaiseCardCounts();
    }

    void ClearTopology() {
        PartOfTitle = null;
        CycleNote = null;
        _blockedBy.Clear();
        RaiseCardCounts();
    }

    void RaiseCardCounts() {
        SyncPartsFold();
        this.RaisePropertyChanged(nameof(PartsHeader));
        this.RaisePropertyChanged(nameof(PartsAccessibleName));
        this.RaisePropertyChanged(nameof(HasParts));
        this.RaisePropertyChanged(nameof(HasThisSessionParts));
        this.RaisePropertyChanged(nameof(HasBlockers));
        this.RaisePropertyChanged(nameof(HasTopologyNotes));
        this.RaisePropertyChanged(nameof(HasContributors));
        this.RaisePropertyChanged(nameof(ShowsPeople));
    }

    void SyncPartsFold() {
        StableRows.Sync(_thisSessionParts, _parts.Where(p => p.IsThisSession));
        WorkContextPartCount[] counts = [
            new(WorkContextPartMark.Unknown, _parts.Count(p => p.Mark == WorkContextPartMark.Unknown)),
            new(WorkContextPartMark.ThisSession, _thisSessionParts.Count),
            new(WorkContextPartMark.Settled, SettledCount),
        ];
        var present = counts.Where(c => c.Count > 0).ToArray();
        if (present.SequenceEqual(_partCounts)) return;
        _partCounts = present;
        this.RaisePropertyChanged(nameof(PartCounts));
    }

    void RaiseRelated() {
        this.RaisePropertyChanged(nameof(ShowsPullRequestCard));
        this.RaisePropertyChanged(nameof(ShowsLegacyLinkCards));
        this.RaisePropertyChanged(nameof(ShowsPullRequestEmpty));
        this.RaisePropertyChanged(nameof(ShowsPullRequestSection));
    }

    void ApplyReady(WorkContextRead read) {
        ViewerIdentity = read.Viewer;
        ResolveRequesterAvatar();
        // A failed summary keeps the last figures, marked stale with the rest of the pane.
        if (!read.SummaryFailed) ApplySessionFacts(read.Summary);
        if (read.Primary is null) {
            ClearCard();
            ApplyLinks(read);
            Phase = WorkContextPhase.NoWorkItem;
            IsStale = read.SummaryFailed;
            return;
        }

        var requested = read.Primary.WorkItemId;
        var served = read.Item?.WorkItemId;
        var samePrimary = served is not null
            ? Same(served, PrimaryId)
            : Same(requested, _requestedId) || Same(requested, PrimaryId);
        _requestedId = requested;
        if (served is not null) PrimaryId = served;
        else if (!samePrimary) PrimaryId = requested;

        if (read.Item is { } item) ApplyItem(item, read.Assignments);
        else if (!samePrimary) {
            ClearItem();
            Title = read.Primary.Label;
        }

        if (!read.TopologyFailed && read.Topology is { } topology) ApplyTopology(topology);
        else if (!samePrimary) ClearTopology();

        ApplyLinks(read);
        Phase = WorkContextPhase.Ready;
        IsStale = read.ItemFailed || read.TopologyFailed || read.SummaryFailed;
    }

    bool _hasSessionFacts;

    void ApplySessionFacts(SessionSummaryDto? summary) {
        _hasSessionFacts = summary is not null;
        Tokens = SessionFactText.Tokens(summary?.Stats);
        Cost = SessionFactText.Cost(summary?.Stats);
        ContextUse = SessionFactText.Context(summary?.Stats);
        SessionSharing = summary is null ? SessionFactText.Unknown : SessionFactText.SessionSharing(summary);
        AgentSharing = SessionFactText.AgentSharing(summary?.AgentVisibility);
        _storedBranch = FirstNonBlank(summary?.RepoBranch, summary?.Repositories.FirstOrDefault(r => r.IsPrimary)?.Branch);
        _storedRepository = FirstNonBlank(summary?.Repositories.FirstOrDefault(r => r.IsPrimary)?.RepoName, summary?.RepoName);
        if (_isRemote) {
            ResolveBranch();
            if (_dto is not null) ResolveRepository(_dto.RepoPath);
        }
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    void ApplyItem(WorkItemDto item, IReadOnlyList<SessionWorkItemAssignmentDto> assignments) {
        Key = FirstNonBlank(item.Key?.ShortKey);
        // An item with no tracker or generated title arrives with its key as the title.
        var title = FirstNonBlank(item.EnrichedTitle, item.Title) ?? "";
        Title = string.Equals(title, Key, StringComparison.OrdinalIgnoreCase) ? "" : title;
        Overview = item.IsOverviewMechanical ? null : FirstNonBlank(item.Overview);
        ApplyState(item.State?.Kind);

        var attached = new HashSet<string>(assignments.Select(a => a.WorkItemId), StringComparer.Ordinal);
        var parts = item.Parts
            .OrderBy(p => p.Ordinal)
            .Select(p => new WorkContextPartViewModel(p.Title, PartMark(p, attached)))
            .ToList();
        Replace(_parts, parts, p => (p.Title, p.Mark));

        ApplyIssues(item.Links.Where(l => l.Kind == "issue" && l.LinkClass == "link"));

        _contributorRows = item.Contributors;
        RefreshContributors();
        SessionCount = item.SessionCount;
        RaiseCardCounts();
    }

    void RefreshContributors() {
        var now = _time.GetUtcNow();
        var people = _contributorRows
            .Select(c => new WorkContextPersonViewModel(PersonName(c),
                _viewer?.Owns(c.UserId) == true ? _viewer.AvatarUrl ?? c.AvatarUrl : c.AvatarUrl,
                c.LastActivityAt, now, c.UserId))
            .ToList();
        Replace(_contributors, people, c => (c.UserId, c.Name, c.AvatarUrl, c.LastActivityText));
        RaiseCardCounts();
    }

    /// DisplayName is null for an owner with no user row; the session requester's email/name
    /// stands in when it is the same person, else the name this pane last showed for the id.
    string PersonName(WorkItemContributorDto contributor) {
        if (_viewer?.Owns(contributor.UserId) == true) return "You";
        if (HumanLabel(contributor.DisplayName) is { } display) return display;
        if (string.Equals(contributor.UserId, _dto?.Requester, StringComparison.Ordinal)
            && HumanLabel(FirstNonBlank(_dto?.RequesterDisplay, _dto?.Requester)) is { } mine)
            return mine;
        if (_contributors.FirstOrDefault(p => string.Equals(p.UserId, contributor.UserId, StringComparison.Ordinal)) is { } previous
            && previous.Name != "You" && HumanLabel(previous.Name) is { } kept)
            return kept;
        return HumanLabel(contributor.UserId) ?? "Someone";
    }

    static string? HumanLabel(string? value) {
        var label = FirstNonBlank(value);
        if (label is null || IsOpaqueUserId(label)) return null;
        return label;
    }

    /// A WorkOS id (`user_` + ULID) is not a name. `github:` ids stay visible: they are the
    /// fallback for a contributor with no display name.
    static bool IsOpaqueUserId(string value) {
        if (!value.StartsWith("user_", StringComparison.Ordinal) || value.Length < 25) return false;
        for (var i = 5; i < value.Length; i++)
            if (!char.IsAsciiLetterOrDigit(value[i])) return false;
        return true;
    }

    static WorkContextPartMark PartMark(WorkItemPartDto part, HashSet<string> attached) =>
        part.IsSettled ? WorkContextPartMark.Settled
        : attached.Contains(part.WorkItemId) ? WorkContextPartMark.ThisSession
        : WorkContextPartMark.Unknown;

    /// The server may add kinds; an unknown one is shown as sent, in the in-flight look.
    void ApplyState(string? kind) {
        var text = kind?.Replace('_', ' ').Trim().ToUpperInvariant();
        StateLabel = string.IsNullOrEmpty(text) ? null : text;
        IsShipped = kind == "shipped";
        IsClosed  = kind == "closed";
    }

    void ApplyIssues(IEnumerable<WorkItemLinkDto> links) {
        var cards = new List<WorkContextLinkViewModel>();
        foreach (var link in links) {
            if (IsDuplicateIssue(cards, link)) continue;
            var title = FirstNonBlank(link.Title) ?? "";
            cards.Add(new WorkContextLinkViewModel("ISSUE", link.ShortKey, title, link.Url, _opener,
                identity: IssueIdentity(link)));
        }
        Replace(_issues, cards, i => (i.Key, i.Title, i.Url, i.Identity));
        Issue = _issues.FirstOrDefault();
        NotifyIdentity();
    }

    /// ShortKey is display text only: two null-URL issues from different repos can share `#42`.
    /// Prefer URL when both have one; otherwise the server's kind/provider/value tuple.
    static bool IsDuplicateIssue(List<WorkContextLinkViewModel> cards, WorkItemLinkDto link) {
        var identity = IssueIdentity(link);
        return cards.Exists(c =>
            link.Url is { Length: > 0 } url && c.Url is { Length: > 0 } existing
                && string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Identity, identity, StringComparison.Ordinal));
    }

    static string IssueIdentity(WorkItemLinkDto link) =>
        string.Join('\u001f', link.Kind, link.Provider, link.Value);

    void ApplyTopology(WorkItemTopologyDto topology) {
        PartOfTitle = topology.PartOf?.Title;
        Replace(_blockedBy, topology.BlockedBy.Select(b => b.Title).ToList(), b => b);
        CycleNote = topology.Cycle switch {
            "cyclic"        => "Dependencies form a cycle",
            "indeterminate" => "Dependencies could not be fully resolved",
            _               => null,
        };
        RaiseCardCounts();
    }

    void ApplyLinks(WorkContextRead read) {
        if (!read.SummaryFailed && read.Summary is { } summary) {
            var primaryRepositories = summary.Repositories.Where(repository => repository.IsPrimary).ToArray();
            if (primaryRepositories.Length == 1) {
                var repo = primaryRepositories[0];
                // The host is inferred from a linked pull request sharing the repository hash, else github.com is assumed.
                var (provider, host) = RepositoryIdentity(summary, repo.RepoHash);
                PrimaryRepository = new(provider, host, repo.Owner, repo.RepoName, repo.RepoHash);
            } else PrimaryRepository = null;

            if (ShowsLegacyLinks) {
                var cards = summary.PullRequests
                    .Select(pr => Link(pr.Number, pr.Title, pr.Url))
                    .ToList();
                if (summary.PrNumber is { } number && !summary.PullRequests.Any(pr => SamePullRequest(pr, summary, number)))
                    cards.Add(Link(number, summary.PrTitle, summary.PrUrl));
                _summaryPrs.Clear();
                _summaryPrs.AddRange(cards);
            } else _summaryPrs.Clear();
        }

        RebuildLinks();
    }

    /// Link cards are the legacy stand-in for the card: the session summary's PRs, never the work
    /// item's.
    void RebuildLinks() {
        Replace(_links, ShowsLegacyLinks ? [.. _summaryPrs] : [], l => (l.Key, l.Title, l.Url));
        RaiseRelated();
    }

    /// A poll that returns the same rows leaves the bound list alone, so the ItemsControl keeps its
    /// containers instead of rebuilding them every 30 seconds.
    static void Replace<T, TKey>(AvaloniaList<T> target, List<T> incoming, Func<T, TKey> key) {
        if (target.Select(key).SequenceEqual(incoming.Select(key))) return;
        target.Clear();
        target.AddRange(incoming);
    }

    WorkContextLinkViewModel Link(int number, string? title, string? url) =>
        new("PULL REQUEST", $"#{number}", title ?? "", url, _opener);

    static (string Provider, string Host) RepositoryIdentity(SessionSummaryDto summary, string repoHash) {
        var link = summary.PullRequests.FirstOrDefault(pr => pr.RepoHash == repoHash && PullRequestWire.SafeLink(pr.Url) is not null);
        if (link is null) return ("github", "github.com");
        var uri = new Uri(PullRequestWire.SafeLink(link.Url)!);
        return (ProviderOf(uri), uri.IdnHost);
    }

    static string ProviderOf(Uri uri) =>
        uri.AbsolutePath.Contains("/-/merge_requests/", StringComparison.Ordinal) ? "gitlab" : "github";

    /// PR numbers are repository-local; without a repository identity on the summary the number
    /// alone decides, which never shows one PR twice.
    internal static bool SamePullRequest(SessionPullRequestDto pr, SessionSummaryDto summary, int number) {
        if (pr.Number != number) return false;
        if (string.IsNullOrEmpty(summary.RepoOwner) || string.IsNullOrEmpty(summary.RepoName)) return true;

        return string.Equals(pr.Owner, summary.RepoOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pr.RepoName, summary.RepoName, StringComparison.OrdinalIgnoreCase);
    }
}
