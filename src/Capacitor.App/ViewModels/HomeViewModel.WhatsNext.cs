using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.NextWork;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class HomeViewModel {
    public const string DefaultGoalWatermark = "Describe a goal, or paste an issue, PR or failing CI link...";
    public const string DuplicateTargetLaunchMessage = "A session is already starting for this item.";

    static readonly TimeSpan TargetLaunchCooldown = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<WorktreeModeChoice> AllWorktreeModes { get; } = [
        new(WorktreeLaunchMode.Automatic, WorktreeLaunchMode.Automatic.MenuLabel(), WorktreeLaunchMode.Automatic.Description()),
        new(WorktreeLaunchMode.SameWorktree, WorktreeLaunchMode.SameWorktree.MenuLabel(), WorktreeLaunchMode.SameWorktree.Description()),
        new(WorktreeLaunchMode.NoBranch, WorktreeLaunchMode.NoBranch.MenuLabel(), WorktreeLaunchMode.NoBranch.Description()),
    ];

    INextWorkSource? _nextWorkSource;
    LocalRepoHashIndex _repoHashIndex = new();
    bool _suppressGoalPastedLinkDetection;
    bool _repoSelectionFromPastedLink;
    string _goalWatermark = DefaultGoalWatermark;
    WorktreeLaunchMode _selectedWorktreeMode = WorktreeLaunchMode.Automatic;
    string? _selectedExistingWorktreePath;
    string? _selectedTargetKey;
    string? _selectedWorkItemId;
    string? _selectedResumeSessionId;
    StartFromBlueprintKind? _selectedBlueprintKind;
    bool _isLoadingWhatsNext;
    bool _hasLoadedWhatsNextOnce;
    IReadOnlyList<NextWorkFeedRowDto> _lastFeedRows = [];
    readonly Dictionary<string, DateTimeOffset> _activeTargetLaunches = new(StringComparer.Ordinal);
    int _whatsNextRefreshGeneration;

    public WhatsNextLaneViewModel WaitingOnYouLane { get; } = new("waiting", "Waiting on you");
    public WhatsNextLaneViewModel FinishLane       { get; } = new("finish", "Finish");
    public WhatsNextLaneViewModel PickUpLane       { get; } = new("pick_up", "Pick up");

    public IReadOnlyList<WhatsNextLaneViewModel> WhatsNextLanes { get; }

    public IReadOnlyList<StartFromBlueprintViewModel> StartFromBlueprints { get; }

    public ReactiveCommand<Unit, Unit>? RefreshWhatsNextCommand { get; private set; }

    public string GoalWatermark {
        get => _goalWatermark;
        private set => this.RaiseAndSetIfChanged(ref _goalWatermark, value);
    }

    public bool RepoSelectionFromPastedLink {
        get => _repoSelectionFromPastedLink;
        private set {
            if (_repoSelectionFromPastedLink == value) return;
            this.RaiseAndSetIfChanged(ref _repoSelectionFromPastedLink, value);
            this.RaisePropertyChanged(nameof(RepositoryChipLabel));
        }
    }

    public string RepositoryChipLabel =>
        string.IsNullOrEmpty(SelectedRepoPath)
            ? "Repo · No repository"
            : RepoSelectionFromPastedLink
                ? $"{RepoLabel.Leaf(SelectedRepoPath)} · from the pasted link"
                : $"Repo · {RepoLabel.Leaf(SelectedRepoPath)}";

    public IReadOnlyList<WorktreeModeChoice> WorktreeModeChoices => AllWorktreeModes;

    public WorktreeLaunchMode SelectedWorktreeMode {
        get => _selectedWorktreeMode;
        set {
            if (_selectedWorktreeMode == value) return;
            this.RaiseAndSetIfChanged(ref _selectedWorktreeMode, value);
            this.RaisePropertyChanged(nameof(WorktreeChipLabel));
        }
    }

    public string? SelectedExistingWorktreePath {
        get => _selectedExistingWorktreePath;
        private set {
            if (string.Equals(_selectedExistingWorktreePath, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _selectedExistingWorktreePath, value);
            this.RaisePropertyChanged(nameof(ContinuesInExistingWorktree));
            this.RaisePropertyChanged(nameof(WorktreeChipLabel));
        }
    }

    public bool ContinuesInExistingWorktree =>
        !string.IsNullOrWhiteSpace(_selectedExistingWorktreePath) && Directory.Exists(_selectedExistingWorktreePath);

    public string WorktreeChipLabel => SelectedWorktreeMode.ChipLabel(ContinuesInExistingWorktree);

    public string? SelectedTargetKey {
        get => _selectedTargetKey;
        private set => this.RaiseAndSetIfChanged(ref _selectedTargetKey, value);
    }

    public string? SelectedWorkItemId {
        get => _selectedWorkItemId;
        private set => this.RaiseAndSetIfChanged(ref _selectedWorkItemId, value);
    }

    public string? SelectedResumeSessionId {
        get => _selectedResumeSessionId;
        private set => this.RaiseAndSetIfChanged(ref _selectedResumeSessionId, value);
    }

    public StartFromBlueprintKind? SelectedBlueprintKind {
        get => _selectedBlueprintKind;
        private set => this.RaiseAndSetIfChanged(ref _selectedBlueprintKind, value);
    }

    public bool IsLoadingWhatsNext {
        get => _isLoadingWhatsNext;
        private set {
            if (_isLoadingWhatsNext == value) return;
            this.RaiseAndSetIfChanged(ref _isLoadingWhatsNext, value);
            this.RaisePropertyChanged(nameof(ShowWhatsNextSkeleton));
        }
    }

    public bool HasAnyWhatsNextCards =>
        WaitingOnYouLane.IsVisible || FinishLane.IsVisible || PickUpLane.IsVisible;

    public bool ShowWhatsNextSkeleton =>
        IsLoadingWhatsNext && !_hasLoadedWhatsNextOnce && !HasAnyWhatsNextCards;

    void InitializeWhatsNext(INextWorkSource? nextWorkSource, LocalRepoHashIndex? repoHashIndex) {
        _nextWorkSource = nextWorkSource;
        _repoHashIndex  = repoHashIndex ?? new LocalRepoHashIndex();

        RefreshWhatsNextCommand = ReactiveCommand.CreateFromTask(RefreshWhatsNextAsync);

        foreach (var lane in WhatsNextLanes) {
            lane.WhenAnyValue(x => x.IsVisible)
                .Subscribe(_ => {
                    this.RaisePropertyChanged(nameof(HasAnyWhatsNextCards));
                    this.RaisePropertyChanged(nameof(ShowWhatsNextSkeleton));
                })
                .DisposeWith(_disposables);
        }

        if (_nextWorkSource is not null) {
            _laneStatus
                .Select(s => s.State == ServerLaneState.Connected)
                .DistinctUntilChanged()
                .Where(connected => connected)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => _ = RefreshWhatsNextAsync())
                .DisposeWith(_disposables);
        }

        if (_directory is not null) {
            _directory.Rows.Connect()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RebuildWhatsNextLanes(_lastFeedRows))
                .DisposeWith(_disposables);
        }

        _ = RefreshWhatsNextAsync();
    }

    IReadOnlyList<StartFromBlueprintViewModel> BuildStartFromBlueprints() => [
        new(
            StartFromBlueprintKind.ReviewPr,
            "Review a PR",
            "Reviewer and driver, closes on sign-off",
            "Paste a pull request URL or number to review...",
            WorktreeLaunchMode.Automatic,
            SelectBlueprint),
        new(
            StartFromBlueprintKind.FixCi,
            "Fix failing CI",
            "Reads the job log first",
            "Paste a failing CI run or PR link, or describe the failure...",
            WorktreeLaunchMode.Automatic,
            SelectBlueprint),
        new(
            StartFromBlueprintKind.ExploreIdea,
            "Explore an idea",
            "No branch until you ask for one",
            "Describe the idea or question you want to explore...",
            WorktreeLaunchMode.NoBranch,
            SelectBlueprint),
    ];

    public void SelectWorktreeMode(WorktreeLaunchMode mode) => SelectedWorktreeMode = mode;

    public void PinLane(string groupKey) => FindLane(groupKey)?.Pin();

    public void ReleaseLane(string groupKey) => FindLane(groupKey)?.Release();

    WhatsNextLaneViewModel? FindLane(string groupKey) => groupKey switch {
        "waiting" => WaitingOnYouLane,
        "finish"  => FinishLane,
        "pick_up" => PickUpLane,
        _         => null,
    };

    public void SelectBlueprint(StartFromBlueprintViewModel blueprint) {
        if (blueprint.IsSelected) {
            blueprint.IsSelected  = false;
            SelectedBlueprintKind = null;
            GoalWatermark         = DefaultGoalWatermark;
            SelectedWorktreeMode  = WorktreeLaunchMode.Automatic;
            return;
        }

        ClearSelectedWhatsNextCard(clearGoal: false);
        foreach (var item in StartFromBlueprints)
            item.IsSelected = ReferenceEquals(item, blueprint);

        SelectedBlueprintKind = blueprint.Kind;
        GoalWatermark         = blueprint.Placeholder;
        SelectedWorktreeMode  = blueprint.DefaultWorktreeMode;
    }

    public async Task SelectWhatsNextCardAsync(WhatsNextCardViewModel card) {
        if (card.IsSelected) {
            var previousPrompt = card.LaunchPrompt;
            ClearSelectedWhatsNextCard(clearGoal: string.Equals(Goal, previousPrompt, StringComparison.Ordinal));
            return;
        }

        foreach (var bp in StartFromBlueprints)
            bp.IsSelected = false;
        SelectedBlueprintKind = null;
        GoalWatermark         = DefaultGoalWatermark;

        foreach (var lane in WhatsNextLanes)
            foreach (var item in lane.AllCards)
                item.IsSelected = ReferenceEquals(item, card);

        SelectedTargetKey       = card.TargetKey;
        SelectedWorkItemId      = card.WorkItemId;
        SelectedResumeSessionId = card.SessionId;

        _suppressGoalPastedLinkDetection = true;
        try {
            Goal = card.LaunchPrompt;
        } finally {
            _suppressGoalPastedLinkDetection = false;
        }

        if (card.ContinuesInSameWorktree && !string.IsNullOrWhiteSpace(card.WorktreePath)) {
            var mainRoot = GitRepository.ResolveMainRepoRoot(card.WorktreePath);
            if (!string.IsNullOrWhiteSpace(mainRoot))
                await SelectRepositoryCoreAsync(mainRoot, fromPastedLink: false, preserveWorktree: true);
            SelectedExistingWorktreePath = card.WorktreePath;
            SelectedWorktreeMode         = WorktreeLaunchMode.Automatic;
            return;
        }

        SelectedExistingWorktreePath = null;
        SelectedWorktreeMode         = WorktreeLaunchMode.Automatic;

        if (!string.IsNullOrWhiteSpace(card.RepoHash) && !RemoteMachineSelected) {
            var (_, localPaths) = await GatherLocalRepoDataAsync();
            if (_repoHashIndex.TryResolveByRepoHash(card.RepoHash, localPaths) is { Length: > 0 } matchedRepo)
                await SelectRepositoryCoreAsync(matchedRepo, fromPastedLink: false, preserveWorktree: false);
        }
    }

    void ClearSelectedWhatsNextCard(bool clearGoal) {
        foreach (var lane in WhatsNextLanes)
            foreach (var item in lane.AllCards)
                item.IsSelected = false;

        SelectedTargetKey            = null;
        SelectedWorkItemId           = null;
        SelectedResumeSessionId      = null;
        SelectedExistingWorktreePath = null;

        if (clearGoal) {
            _suppressGoalPastedLinkDetection = true;
            try {
                Goal = "";
            } finally {
                _suppressGoalPastedLinkDetection = false;
            }
        }
    }

    public async Task RefreshWhatsNextAsync() {
        if (_disposed) return;
        if (_nextWorkSource is null) {
            RebuildWhatsNextLanes(_lastFeedRows);
            return;
        }

        var generation = ++_whatsNextRefreshGeneration;
        IsLoadingWhatsNext = true;
        try {
            var read = await _nextWorkSource.ReadAsync(_shutdown);
            if (_disposed || generation != _whatsNextRefreshGeneration) return;

            if (read.Kind == NextWorkReadKind.Ready) {
                _lastFeedRows = read.Items;
                _hasLoadedWhatsNextOnce = true;
                RebuildWhatsNextLanes(_lastFeedRows);
            } else if (read.Kind == NextWorkReadKind.Unavailable) {
                _lastFeedRows = [];
                _hasLoadedWhatsNextOnce = true;
                RebuildWhatsNextLanes(_lastFeedRows);
            } else {
                RebuildWhatsNextLanes(_lastFeedRows);
            }
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
        } finally {
            if (!_disposed && generation == _whatsNextRefreshGeneration)
                IsLoadingWhatsNext = false;
        }
    }

    void RebuildWhatsNextLanes(IReadOnlyList<NextWorkFeedRowDto> serverRows) {
        var waiting = new List<WhatsNextCardViewModel>();
        var finish  = new List<WhatsNextCardViewModel>();
        var pickUp  = new List<WhatsNextCardViewModel>();

        var seenTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in serverRows) {
            var group = row.ResolvedGroup;
            if (string.Equals(group, "housekeeping", StringComparison.OrdinalIgnoreCase)
                || row.TargetKey.StartsWith("evals:", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.TargetKey))
                seenTargetKeys.Add(row.TargetKey);
            if (row.ResolvedSessionId is { Length: > 0 } sid)
                seenSessionIds.Add(sid);

            var card = BuildCardViewModel(row);
            switch (group) {
                case "waiting": waiting.Add(card); break;
                case "finish":  finish.Add(card);  break;
                default:        pickUp.Add(card);  break;
            }
        }

        foreach (var continueCard in BuildContinueSessionCards(seenTargetKeys, seenSessionIds))
            finish.Add(continueCard);

        WaitingOnYouLane.UpdateCards(waiting);
        FinishLane.UpdateCards(finish);
        PickUpLane.UpdateCards(pickUp);
    }

    WhatsNextCardViewModel BuildCardViewModel(NextWorkFeedRowDto row) {
        var sessionId    = row.ResolvedSessionId;
        var worktreePath = ResolveLocalWorktreePath(row.WorktreePath, sessionId);
        var continues    = !string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath);
        var title        = string.IsNullOrWhiteSpace(row.TargetLabel) ? row.TargetId : row.TargetLabel.Trim();
        var subtitle     = BuildCardSubtitle(row, continues);
        var tooltip      = BuildCardTooltip(row, title, continues);
        var prompt       = NextWorkPromptBuilder.BuildForRow(row);
        var targetKey    = row.TargetKey;

        WhatsNextCardViewModel? card = null;
        card = new WhatsNextCardViewModel(
            targetKey: targetKey,
            group: row.ResolvedGroup,
            arm: row.Arm,
            title: title,
            subtitle: subtitle,
            tooltipText: tooltip,
            launchPrompt: prompt,
            repoHash: row.RepoHash,
            workItemId: row.ResolvedWorkItemId,
            sessionId: sessionId,
            worktreePath: worktreePath,
            continuesInSameWorktree: continues,
            onSelect: c => _ = SelectWhatsNextCardAsync(c)) {
            IsSelected  = string.Equals(targetKey, SelectedTargetKey, StringComparison.Ordinal),
            IsLaunching = IsTargetLaunchInFlight(targetKey),
        };
        return card;
    }

    IEnumerable<WhatsNextCardViewModel> BuildContinueSessionCards(
            HashSet<string> seenTargetKeys,
            HashSet<string> seenSessionIds) {
        if (_directory is null) yield break;

        foreach (var row in _directory.Rows.Items) {
            if (SessionStatusDots.IsTerminal(row.Status)) continue;
            var sessionId = row.SessionId;
            var targetKey = !string.IsNullOrWhiteSpace(sessionId) ? $"session:{sessionId}" : $"agent:{row.Id}";
            if (!seenTargetKeys.Add(targetKey)) continue;
            if (!string.IsNullOrWhiteSpace(sessionId) && !seenSessionIds.Add(sessionId)) continue;

            var localAgent   = _daemon.Agents.Items.FirstOrDefault(a => string.Equals(a.Id, row.Id, StringComparison.Ordinal));
            var worktreePath = localAgent?.WorktreePath ?? (row.Origin == AgentOrigin.Local ? row.RepoPath : null);
            var continues    = !string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath);
            var title        = string.IsNullOrWhiteSpace(row.Title) ? $"{row.Vendor} session" : row.Title.Trim();
            var statusDetail = string.IsNullOrWhiteSpace(row.Branch) ? $"Status: {row.Status}." : $"Branch {row.Branch} · {row.Status}.";
            var subtitle     = continues && !statusDetail.Contains("same worktree", StringComparison.OrdinalIgnoreCase)
                ? $"{statusDetail} Continues in the same worktree."
                : statusDetail;
            var repoHash = !string.IsNullOrWhiteSpace(row.RepoOwner) && !string.IsNullOrWhiteSpace(row.RepoName)
                ? RepoHashHelper.ComputeRepoHash(row.RepoOwner, row.RepoName)
                : !string.IsNullOrWhiteSpace(row.RepoPath)
                    ? _repoHashIndex.TryGetRepoHash(GitRepository.ResolveMainRepoRoot(row.RepoPath))
                    : null;
            var prompt = $"Continue work on {title}.";

            yield return new WhatsNextCardViewModel(
                targetKey: targetKey,
                group: "finish",
                arm: "ContinueWorking",
                title: title,
                subtitle: subtitle,
                tooltipText: $"{title}\n{subtitle}",
                launchPrompt: prompt,
                repoHash: repoHash,
                workItemId: null,
                sessionId: sessionId,
                worktreePath: worktreePath,
                continuesInSameWorktree: continues,
                onSelect: c => _ = SelectWhatsNextCardAsync(c)) {
                IsSelected  = string.Equals(targetKey, SelectedTargetKey, StringComparison.Ordinal),
                IsLaunching = IsTargetLaunchInFlight(targetKey),
            };
        }
    }

    string? ResolveLocalWorktreePath(string? rowWorktreePath, string? sessionId) {
        if (!string.IsNullOrWhiteSpace(rowWorktreePath) && Directory.Exists(rowWorktreePath))
            return rowWorktreePath;

        if (string.IsNullOrWhiteSpace(sessionId)) return rowWorktreePath;

        var localAgent = _daemon.Agents.Items.FirstOrDefault(
            a => string.Equals(a.SessionId, sessionId, StringComparison.Ordinal));
        if (localAgent is { WorktreePath: { Length: > 0 } wt } && Directory.Exists(wt))
            return wt;

        if (_directory is not null) {
            var dirRow = _directory.Rows.Items.FirstOrDefault(
                r => r.Origin == AgentOrigin.Local && string.Equals(r.SessionId, sessionId, StringComparison.Ordinal));
            if (dirRow is { RepoPath: { Length: > 0 } rp } && Directory.Exists(rp))
                return rp;
        }

        return rowWorktreePath;
    }

    static string BuildCardSubtitle(NextWorkFeedRowDto row, bool continuesInSameWorktree) {
        var baseText = !string.IsNullOrWhiteSpace(row.Because)
            ? row.Because.Trim()
            : row.Evidence.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Summary))?.Summary.Trim() ?? "";

        if (continuesInSameWorktree && !baseText.Contains("same worktree", StringComparison.OrdinalIgnoreCase)) {
            if (baseText.Length == 0) return "Continues in the same worktree.";
            var separator = baseText.EndsWith('.') ? " " : ". ";
            return $"{baseText}{separator}Continues in the same worktree.";
        }

        return baseText;
    }

    static string BuildCardTooltip(NextWorkFeedRowDto row, string title, bool continuesInSameWorktree) {
        var lines = new List<string> { title };
        if (!string.IsNullOrWhiteSpace(row.Because))
            lines.Add(row.Because.Trim());
        foreach (var ev in row.Evidence) {
            if (!string.IsNullOrWhiteSpace(ev.Summary)
                && !string.Equals(ev.Summary.Trim(), row.Because.Trim(), StringComparison.OrdinalIgnoreCase)) {
                lines.Add(ev.Summary.Trim());
            }
        }
        if (continuesInSameWorktree)
            lines.Add("Continues in the same worktree.");
        return string.Join("\n", lines);
    }

    void OnGoalChangedByUser(string newGoal) {
        if (_suppressGoalPastedLinkDetection) return;

        if (string.IsNullOrWhiteSpace(newGoal) && SelectedTargetKey is not null)
            ClearSelectedWhatsNextCard(clearGoal: false);

        if (PastedLinkDetector.TryDetect(newGoal) is { } match)
            _ = TryAutoSelectRepoFromPastedLinkAsync(match);
    }

    async Task TryAutoSelectRepoFromPastedLinkAsync(PastedLinkMatch match) {
        if (RemoteMachineSelected) return;

        var (_, localPaths) = await GatherLocalRepoDataAsync();
        if (localPaths.Count == 0) return;

        var issueRepoMap = BuildIssueKeyRepoHashMap(_lastFeedRows);
        var matchedPath  = _repoHashIndex.TryResolvePastedLink(match, localPaths, issueRepoMap);
        if (string.IsNullOrWhiteSpace(matchedPath)) return;

        await SelectRepositoryCoreAsync(matchedPath, fromPastedLink: true, preserveWorktree: false);
    }

    static Dictionary<string, string> BuildIssueKeyRepoHashMap(IReadOnlyList<NextWorkFeedRowDto> rows) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) {
            if (string.IsNullOrWhiteSpace(row.RepoHash)) continue;
            foreach (Match m in IssueKeyTokenRegex().Matches(row.TargetLabel))
                map.TryAdd(m.Value.ToUpperInvariant(), row.RepoHash);
            foreach (var ev in row.Evidence) {
                if (!string.IsNullOrWhiteSpace(ev.Id))
                    map.TryAdd(ev.Id.Trim().ToUpperInvariant(), row.RepoHash);
            }
        }
        return map;
    }

    (bool Borrowed, string? BorrowCwd) ResolveWorktreeBorrowForLaunch(string repoPath) {
        if (string.IsNullOrWhiteSpace(repoPath)) return (false, null);

        return SelectedWorktreeMode switch {
            WorktreeLaunchMode.NoBranch     => (true, repoPath),
            WorktreeLaunchMode.SameWorktree => (true, ContinuesInExistingWorktree ? _selectedExistingWorktreePath! : repoPath),
            WorktreeLaunchMode.Automatic when ContinuesInExistingWorktree => (true, _selectedExistingWorktreePath!),
            _ => (false, null),
        };
    }

    bool IsTargetLaunchInFlight(string? targetKey) {
        if (string.IsNullOrWhiteSpace(targetKey)) return false;
        lock (_launchTrackingLock) {
            if (!_activeTargetLaunches.TryGetValue(targetKey, out var startedAt)) return false;
            if (_time.GetUtcNow() - startedAt <= TargetLaunchCooldown) return true;
            _activeTargetLaunches.Remove(targetKey);
            return false;
        }
    }

    void MarkTargetLaunching(string? targetKey, bool launching) {
        if (string.IsNullOrWhiteSpace(targetKey)) return;
        lock (_launchTrackingLock) {
            if (launching) _activeTargetLaunches[targetKey] = _time.GetUtcNow();
            else _activeTargetLaunches.Remove(targetKey);
        }
        foreach (var lane in WhatsNextLanes)
            foreach (var card in lane.AllCards)
                if (string.Equals(card.TargetKey, targetKey, StringComparison.Ordinal))
                    card.IsLaunching = launching;
    }

    [GeneratedRegex(@"\b[A-Za-z]{2,10}-\d+\b")]
    private static partial Regex IssueKeyTokenRegex();
}
