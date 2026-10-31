using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel : ReactiveObject {
    static readonly TimeSpan SnapshotGrace = TimeSpan.FromMinutes(5);
    sealed record Position(string Section, string? Thread, string Resolved, double Scroll, long Touched);
    readonly IPullRequestSource _source;
    readonly LastReads<PullRequestSnapshot>? _snapshots;
    readonly Func<string?, string?> _readBranch;
    readonly bool _hasCustomBranchReader;
    readonly TimeProvider _time;
    readonly IUrlOpener _opener;
    readonly Action _openReader;
    readonly Action<string>? _linksChanged;
    readonly Func<PullRequestRepository?>? _primaryRepo;
    readonly IPullRequestReaders? _readers;
    readonly CompositeDisposable _subscriptions = new();
    readonly HashSet<Task> _tasks = [];
    readonly List<CancellationTokenSource> _retired = [];
    readonly Dictionary<string, PullRequestSectionState> _sections = new(StringComparer.Ordinal);
    readonly Dictionary<PullRequestSubjectDto, Position> _positions = [];
    readonly Dictionary<string, DateTime> _rateLimits = new(StringComparer.Ordinal);
    readonly HashSet<string> _pageRequests = new(StringComparer.Ordinal);
    readonly AvaloniaList<PullRequestChoice> _choices = [];
    readonly List<PullRequestLinkDto> _sessionItems = [];
    /// Lifecycles the card has read for this session's PRs: the server's list carries none, so an
    /// overview is what tells a merged PR from an open one.
    readonly Dictionary<PullRequestSubjectDto, string> _lifecycles = [];
    // A subject, not WhenAnyValue: that needs ReactiveUI's global init, which a headless test run does not reliably prime first.
    readonly BehaviorSubject<bool> _hasPullRequest = new(false);
    readonly ITimer _timer;
    CancellationTokenSource _cancel = new();
    long _generation;
    string? _session;
    string? _branch;
    PullRequestChoice? _selected;
    bool _explicitSelection;
    PullRequestSubjectDto? _requestedSubject;
    bool _updatingChoices;
    PullRequestOverviewDto? _overview;
    PullRequestRead<PullRequestOverviewDto>? _overviewRead;
    long _accessStarted;
    int _accessSeconds;
    bool _grace;
    string? _staleReason;
    bool _hasDisplayed;
    bool _foreground;
    bool _readerVisible;
    bool _masked = true;
    bool _refreshing;
    bool _overviewPending;
    bool _queuedRefresh;
    bool _stopped;
    string? _worktree;
    bool _disposed;
    bool _legacy;
    bool _hasListed;
    long? _lastRefresh;
    long? _lastOverview;
    DateTime? _retryAt;
    DateTime? _listRetryAt;
    int _pollAfter = 30;
    string _section = "overview";
    string? _thread;
    internal const string NoPullRequestsNote = "No pull requests yet.";
    string _notice = WorkContextViewModel.WaitingNote;
    PullRequestReaderNote? _readerNote;

    public IAvaloniaReadOnlyList<PullRequestChoice> Choices => _choices;
    public PullRequestChoice? Selected {
        get => _selected;
        set { if (!_updatingChoices) Select(value, explicitSelection: true); }
    }
    public string Notice => _notice;
    public string ReaderNote => _readerNote?.Text ?? "";
    public bool HasReaderNote => _readerNote is not null;
    public bool ShowsInstallTool => _readerNote?.InstallUrl is not null;
    public string InstallToolLabel => _readerNote?.InstallLabel ?? "";
    public bool IsReading => _refreshing || _queuedRefresh || _overviewPending || _pageRequests.Count > 0;
    bool _userRefresh;
    /// Progress for a refresh someone asked for; the background poll reads silently.
    public bool ShowsRefreshing => _userRefresh && IsReading;
    public bool HasChoice => _selected is not null;
    public bool HasPullRequest => _choices.Any(choice => choice.IsAvailable);
    public IObservable<bool> HasPullRequestChanges => _hasPullRequest;
    public bool HasListed => _hasListed;
    public bool IsLegacy => _legacy;
    public string Section => _section;
    public double ScrollOffset { get; set; }
    public bool CanReveal => _foreground && !_masked && !_grace && Remaining > 0 && !_legacy;
    public bool CanDisplay => _foreground && !_masked && (Remaining > 0
        || (_grace || RenewingSnapshot) && SnapshotDisplayRemaining > 0);
    public bool CanNavigate => CanDisplay && !_legacy;
    bool CanDisplayReader => _readerVisible && CanDisplay && (CanReveal || !ReadsRows || _sections.ContainsKey(SectionKey));
    double Remaining => Math.Max(0, _accessSeconds - _time.GetElapsedTime(_accessStarted).TotalSeconds);
    bool RenewingSnapshot => _hasDisplayed && _accessSeconds > 0 && _overviewPending;
    // Both in-flight renewal and failed-renewal grace end at the same access-based deadline.
    double SnapshotDisplayRemaining => _accessSeconds + SnapshotGrace.TotalSeconds - _time.GetElapsedTime(_accessStarted).TotalSeconds;
    DateTime? HostRateLimitRetryAt => _selected is { } choice && _rateLimits.TryGetValue(RateLimitKey(choice.Subject), out var rateLimit) ? rateLimit : null;
    DateTime? ReadRetryAt => HostRateLimitRetryAt is { } rateLimit && (_retryAt is null || rateLimit > _retryAt) ? rateLimit : _retryAt;
    static string RateLimitKey(PullRequestSubjectDto subject) => (subject.Provider + "|" + subject.Host).ToLowerInvariant();
    string SectionKey => _section == "thread_comments" ? _section + ":" + _thread : _section == "threads" ? _section + ":" + _resolved : _section;
    string _resolved = "unresolved";
    public bool IncludeResolved {
        get => _resolved == "all";
        set {
            if (value == IncludeResolved || !CanReveal) return;
            _resolved = value ? "all" : "unresolved";
            this.RaisePropertyChanged();
            ShowSection("threads");
        }
    }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenReaderCommand { get; }
    public ReactiveCommand<string, Unit> ShowSectionCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadEarlierCommand { get; }
    public ReactiveCommand<PullRequestRow, Unit> ExpandThreadCommand { get; }
    public ReactiveCommand<PullRequestRow, Unit> OpenRowCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public ReactiveCommand<string, Unit> OpenBodyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    public ReactiveCommand<Unit, Unit> LinkGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallToolCommand { get; }
    /// Puts <see cref="FixPrompt"/> in the session's composer for the user to send.
    public ReactiveCommand<Unit, Unit> FixCommand { get; }
    /// Whether this viewer's composer can take Fix's prompt: a read-only participant's cannot send it.
    readonly Func<bool> _canRequestFix;

    public PullRequestContextViewModel(IObservable<AgentStatusDto?> presence, IPullRequestSource source, TimeProvider time, IUrlOpener opener,
        Action openReader, Action? signIn = null, Action? linkGitHub = null, IObservable<Unit>? signInCompleted = null, Func<PullRequestRepository?>? primaryRepo = null, Func<string?, string?>? readBranch = null,
        LastReads<PullRequestSnapshot>? snapshots = null, Action<string>? linksChanged = null,
        Action<string>? requestFix = null, Func<bool>? canRequestFix = null) {
        _source = source;
        _snapshots = snapshots;
        _canRequestFix = canRequestFix ?? (() => requestFix is not null);
        _linksChanged = linksChanged;
        _readBranch = readBranch ?? GitRepository.CurrentBranch;
        _hasCustomBranchReader = readBranch is not null;
        _time = time;
        _opener = opener;
        _openReader = openReader;
        _primaryRepo = primaryRepo;
        _readers = source as IPullRequestReaders;
        RefreshCommand = ReactiveCommand.Create(Refresh);
        OpenReaderCommand = ReactiveCommand.Create(OpenReader);
        ShowSectionCommand = ReactiveCommand.Create<string>(ShowSection);
        LoadMoreCommand = ReactiveCommand.Create(() => { if (CurrentSection?.Next is { } cursor) RequestPage(cursor); });
        ReloadEarlierCommand = ReactiveCommand.Create(() => { if (CurrentSection?.Evicted is { } cursor) RequestPage(cursor, earlier: true); });
        ExpandThreadCommand = ReactiveCommand.Create<PullRequestRow>(row => {
            if (!CanReveal || !row.IsThread) return;
            _thread = row.Id;
            ShowSection("thread_comments");
        });
        OpenRowCommand = ReactiveCommand.Create<PullRequestRow>(row => {
            if (CanDisplayReader) LinkPolicy.Open(_opener, row.IsCheck ? PullRequestWire.CheckLink(row.Url) : _selected is null ? null : PrLink(row.Url, _selected.Subject));
        });
        OpenGitHubCommand = ReactiveCommand.Create(OpenSource);
        OpenBodyLinkCommand = ReactiveCommand.Create<string>(url => { if (CanDisplayReader) LinkPolicy.Open(_opener, PullRequestWire.BodyLink(url)); });
        SignInCommand = ReactiveCommand.Create(() => signIn?.Invoke());
        LinkGitHubCommand = ReactiveCommand.Create(() => linkGitHub?.Invoke());
        InstallToolCommand = ReactiveCommand.Create(() => LinkPolicy.Open(_opener, _readerNote?.InstallUrl));
        _subscriptions.Add(InstallToolCommand);
        FixCommand = ReactiveCommand.Create(() => { if (ShowsFix && FixPrompt is { } prompt) requestFix?.Invoke(prompt); });
        _subscriptions.Add(FixCommand);
        _subscriptions.Add(RefreshCommand); _subscriptions.Add(OpenReaderCommand); _subscriptions.Add(ShowSectionCommand);
        _subscriptions.Add(LoadMoreCommand); _subscriptions.Add(ReloadEarlierCommand); _subscriptions.Add(ExpandThreadCommand);
        _subscriptions.Add(OpenRowCommand); _subscriptions.Add(OpenGitHubCommand); _subscriptions.Add(OpenBodyLinkCommand);
        _subscriptions.Add(SignInCommand); _subscriptions.Add(LinkGitHubCommand);
        _timer = time.CreateTimer(_ => Dispatcher.UIThread.Post(Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        presence.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(dto => {
            if (_disposed || dto is null) return;
            _branch = dto.Branch;
            // The presented checkout, not the snapshot: a borrowed reviewer's snapshot is detached,
            // so only BorrowedFrom still has the branch live discovery matches.
            _worktree = CheckoutLabel.CheckoutPathFor(dto);
            if (dto.SessionId is not { Length: > 0 } id || _session == id) return;
            var snapshot = _snapshots?.Get(id);
            CancelReads();
            if (_session is not null) _requestedSubject = null;
            _session = id;
            _choices.Clear();
            _sessionItems.Clear();
            _selected = null;
            _explicitSelection = false;
            _positions.Clear();
            _lifecycles.Clear();
            ClearProtected();
            _stopped = false;
            _retryAt = null;
            _listRetryAt = null;
            _pollAfter = 30;
            _queuedRefresh = false;
            _refreshPage = false;
            _hasListed = false;
            ApplyRequestedSubject();
            _lastRefresh = null;
            SetNotice(snapshot is null && _selected is null ? NoPullRequestsNote : "");
            if (snapshot is not null) Seed(snapshot);
            RequestRefresh();
        }).DisposeWith(_subscriptions);
        signInCompleted?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => {
            if (_session is null || _disposed) return;
            CancelReads(); ClearProtected(); _source.ResetSession(_session); _stopped = false; _lastRefresh = null; RequestRefresh(manual: true);
        }).DisposeWith(_subscriptions);
    }

    internal void ClearSession() {
        if (_disposed) return;
        CancelReads();
        _session = null;
        _requestedSubject = null;
        _choices.Clear();
        _sessionItems.Clear();
        _selected = null;
        _explicitSelection = false;
        _positions.Clear();
        _lifecycles.Clear();
        _hasListed = true;
        _queuedRefresh = false;
        _userRefresh = false;
        _stopped = false;
        _retryAt = null;
        _listRetryAt = null;
        _lastRefresh = null;
        ClearProtected();
        SetNotice(WorkContextViewModel.WaitingNote);
    }

    public void SetForeground(bool foreground) {
        if (_disposed || _foreground == foreground) return;
        _foreground = foreground;
        CancelReads();
        _accessSeconds = 0;
        _masked = true;
        _grace = false;
        _staleReason = null;
        Notify();
        // Nothing polls in the background, and the return lists afresh anyway.
        if (!foreground) { _queuedRefresh = false; return; }
        ShowLastOverview();
        // A null _lastRefresh means this session has never refreshed yet, so the coming discovery is
        // already fresh; force only a genuine return, where a prior refresh could have gone stale.
        if (_lastRefresh is not null) _refreshDiscovery = true;
        _lastRefresh = null; _lastOverview = null; RequestRefresh();
    }
    /// The backoff a failed discovery or list set was waiting for a server that was not there; it is back, so the
    /// reads run now through the same path and checks as any refresh, and whatever is shown stays until they answer.
    /// Retries the server stopped stay stopped until the user asks: a queued refresh there would never run and would
    /// leave the panel reading forever. A read already in flight gets one follow-up instead.
    public void Reconnected() {
        if (_disposed) return;
        _refreshDiscovery = true;
        if (!_stopped) {
            _listRetryAt = null;
            _lastRefresh = null;
            _queuedRefresh = _refreshing;
        }
        RequestRefresh();
        Notify();
    }
    /// A list read describes the session with the primary repository it saw, and the registry's
    /// live discovery needs one: list again once the hint arrives or names another repository.
    public void PrimaryRepositoryChanged() {
        if (_disposed || !_foreground || _stopped || _session is null) return;
        _queuedRefresh = true;
        RequestRefresh();
        Notify();
    }
    /// The user's own refresh: rediscovers support and reloads the list, overview and open section.
    public void Refresh() { _userRefresh = true; RequestRefresh(manual: true); Notify(); }
    /// On a legacy or unsupported capability the reader would open onto a notice and nothing else,
    /// so a caller with a PR in hand opens its URL instead.
    public bool CanOpenReader => HasPullRequest && !_legacy;
    /// Title opens Overview; checks/reviews rows use <see cref="ShowSection"/> for their tabs.
    public void OpenReader() {
        if (_disposed || !CanOpenReader) return;
        if (CanReveal) { ShowSection("overview"); return; }
        _section = "overview";
        _thread = null;
        ScrollOffset = 0;
        _openReader();
        SetReaderVisible(true);
    }
    /// The selected PR on its host, in the browser.
    public void OpenSource() {
        if (_selected is { IsAvailable: true } choice) LinkPolicy.Open(_opener, PrLink(choice.Link.Url, choice.Subject));
    }
    public void SetReaderVisible(bool visible) {
        if (_readerVisible == visible) return;
        _readerVisible = visible;
        Notify();
        if (visible && CanReveal && ReadsRows) LoadOrRefreshSection();
    }
    public void SelectSubject(PullRequestSubjectDto subject) {
        if (_disposed) return;
        _requestedSubject = subject;
        ApplyRequestedSubject();
    }

    void ApplyRequestedSubject() {
        if (_session is null || _requestedSubject is not { } subject) return;
        var choice = _choices.FirstOrDefault(item => item.Subject == subject);
        if (choice is null) {
            // A fresh list confirms availability; never fall back to a different PR while loading.
            choice = new PullRequestChoice(new PullRequestLinkDto {
                Provider = subject.Provider, Host = subject.Host, RepoHash = subject.RepoHash,
                Owner = subject.Owner, RepoName = subject.RepoName, Number = subject.Number,
            }, IsAvailable: false);
            _choices.Add(choice);
        }
        _explicitSelection = true;
        if (!_hasListed) {
            // Pin the requested subject without cancelling the initial link discovery.
            _selected = choice;
            if (_notice == NoPullRequestsNote) _notice = "";
            this.RaisePropertyChanged(nameof(Selected));
            Notify();
            return;
        }
        Select(choice, explicitSelection: true);
    }

    void Select(PullRequestChoice? choice, bool explicitSelection = false) {
        if (_disposed) return;
        if (choice is not null && !_choices.Contains(choice)) return;
        if (explicitSelection) {
            _requestedSubject = choice?.Subject;
            _explicitSelection = true;
        }
        if (choice?.Subject == _selected?.Subject) return;
        _explicitSelection = explicitSelection;
        if (_selected is not null) {
            if (_positions.Count >= 20 && !_positions.ContainsKey(_selected.Subject)) _positions.Remove(_positions.MinBy(x => x.Value.Touched).Key);
            _positions[_selected.Subject] = new(_section, _thread, _resolved, ScrollOffset, _time.GetTimestamp());
        }
        CancelReads();
        _selected = choice;
        ClearProtected();
        _retryAt = null;
        _section = "overview"; _thread = null; _resolved = "unresolved"; ScrollOffset = 0;
        if (choice is not null && _positions.TryGetValue(choice.Subject, out var position)) {
            _section = position.Section; _thread = position.Thread; _resolved = position.Resolved; ScrollOffset = position.Scroll;
        }
        if (choice is not null) _notice = ReadRetryAt > _time.GetUtcNow().UtcDateTime
            ? "GitHub reads are paused temporarily. Retry after the cooldown." : "";
        this.RaisePropertyChanged(nameof(Selected));
        Notify();
        RequestOverview();
    }
    void ShowSection(string section) {
        if (!CanNavigate || section is not ("overview" or "conflicts" or "checks" or "reviewers" or "reviews" or "threads" or "thread_comments" or "conversation")) return;
        _section = section;
        ScrollOffset = 0;
        _openReader();
        _readerVisible = true;
        Notify();
        if (!ReadsRows) return;
        if (CanReveal) LoadOrRefreshSection();
        else if (CurrentSection is null && !(ReadRetryAt > _time.GetUtcNow().UtcDateTime)) { _lastOverview = null; RequestOverview(); }
    }
    /// A tab the poll skipped while it was hidden comes back stale; the summary stops trusting rows
    /// older than 30s, so rows kept past that would disagree with it.
    void LoadOrRefreshSection() {
        if (CurrentSection is not { } state) RequestPage(null);
        else if (_headRestartSection == SectionKey || state.Completed is not { } at || _time.GetUtcNow().UtcDateTime - at >= RowsFreshFor) RequestPage(null, refresh: true);
    }
    internal static readonly TimeSpan RowsFreshFor = TimeSpan.FromSeconds(30);
    void ScheduleTick() {
        if (_disposed) return;
        var next = double.PositiveInfinity;
        if (_foreground && _session is not null) {
            if (!_stopped && !_refreshing) {
                var pollGap = _queuedRefresh ? 3 : Math.Max(30, _pollAfter);
                next = _lastRefresh is { } last ? pollGap - _time.GetElapsedTime(last).TotalSeconds : 1;
                if (HostRateLimitRetryAt is { } retry && retry > _time.GetUtcNow().UtcDateTime)
                    next = Math.Max(next, (retry - _time.GetUtcNow().UtcDateTime).TotalSeconds);
                if (_listRetryAt is { } listRetry && listRetry > _time.GetUtcNow().UtcDateTime)
                    next = Math.Max(next, (listRetry - _time.GetUtcNow().UtcDateTime).TotalSeconds);
            }
            if (!_masked && _accessSeconds > 0)
                next = Math.Min(next, _grace ? SnapshotDisplayRemaining : Remaining > 0 ? Remaining : RenewingSnapshot ? SnapshotDisplayRemaining : 0);
            if (!_masked && !_grace && _accessSeconds > 0 && !_legacy && !_stopped && !_overviewPending && _selected is { IsAvailable: true }) {
                var overviewGap = _lastOverview is { } last ? 15 - _time.GetElapsedTime(last).TotalSeconds : 0;
                var overviewDue = Math.Max(Remaining - 5, overviewGap);
                if (ReadRetryAt is { } retry) overviewDue = Math.Max(overviewDue, (retry - _time.GetUtcNow().UtcDateTime).TotalSeconds);
                next = Math.Min(next, overviewDue);
            }
        }
        _timer.Change(double.IsPositiveInfinity(next) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Max(1, next)), Timeout.InfiniteTimeSpan);
    }

    void Tick() {
        if (_disposed) return;
        TickCore();
        ScheduleTick();
    }

    void TickCore() {
        if (_disposed || !_foreground) return;
        if (!_masked && _accessSeconds > 0 && SnapshotDisplayRemaining <= 0) {
            _masked = true; _grace = false; _staleReason = null; Notify();
        }
        if (!_masked && !_grace && _accessSeconds > 0 && Remaining <= 5 && !_legacy && !_stopped && !_overviewPending
            && !(ReadRetryAt > _time.GetUtcNow().UtcDateTime)) RequestOverview();
        if (!(HostRateLimitRetryAt > _time.GetUtcNow().UtcDateTime) && !(_listRetryAt > _time.GetUtcNow().UtcDateTime) && !_stopped
            && (_queuedRefresh || _lastRefresh is null || _time.GetElapsedTime(_lastRefresh.Value).TotalSeconds >= Math.Max(30, _pollAfter))) RequestRefresh();
        if (!_masked && _accessSeconds > 0 && Remaining <= 0 && !_grace) {
            if (!RenewingSnapshot) EnterGrace();
            else Notify();
        }
    }
    void EnterGrace(string? reason = null) {
        if (!_hasDisplayed || !_foreground || _masked || SnapshotDisplayRemaining <= 0) { _masked = true; Notify(); return; }
        _grace = true;
        if (reason is not null) _staleReason = reason;
        SetNotice("");
    }
    bool AcceptAccess<T>(PullRequestRead<T> read) where T : class {
        if (!read.CanReveal(_time) || read.Subject != _selected?.Subject) return false;
        _accessStarted = read.RequestStarted;
        _accessSeconds = read.AccessValidForSeconds;
        _masked = false;
        _grace = false;
        _staleReason = read.Kind == PullRequestReadKind.Stale ? Reason(read) : null;
        _hasDisplayed = true;
        RememberRetry(read);
        _pollAfter = Math.Max(15, read.PollAfterSeconds);
        return true;
    }
    void RememberRetry<T>(PullRequestRead<T> read) where T : class {
        var retry = read.RetryAt;
        if (_retryAt is null || _retryAt <= _time.GetUtcNow().UtcDateTime || retry > _retryAt) _retryAt = retry;
        if (retry is not { } until || read.Reason is not ("rate_limited" or "budget_exhausted") || read.Subject is not { } subject) return;
        foreach (var expired in _rateLimits.Where(pair => pair.Value <= _time.GetUtcNow().UtcDateTime).Select(pair => pair.Key).ToArray()) _rateLimits.Remove(expired);
        var key = RateLimitKey(subject);
        if (!_rateLimits.TryGetValue(key, out var previous) || until > previous) _rateLimits[key] = until;
    }
    void ClearProtected() {
        _overview = null; _overviewRead = null; _sections.Clear(); _accessSeconds = 0;
        _reviewerRows.Clear(); _checkRows.Clear(); _discussionRows.Clear();
        _masked = true; _grace = false; _staleReason = null; _hasDisplayed = false;
        Notify();
    }
    void CancelReads() {
        _generation++;
        _cancel.Cancel();
        if (_tasks.Count == 0) _cancel.Dispose();
        else _retired.Add(_cancel);
        _cancel = new();
        _refreshing = false; _overviewPending = false; _pageRequests.Clear(); _lastOverview = null;
        // The once-only head recovery belongs to the read just cancelled. A later subject
        // has to be allowed its own, or its section stops instead of reloading.
        _headRestartSection = null;
    }
    void Start(Func<CancellationToken, Task<Action>> operation, Action settled) {
        var generation = _generation;
        var token = _cancel.Token;
        var task = Run();
        _tasks.Add(task);
        _ = task.ContinueWith(done => Dispatcher.UIThread.Post(() => {
            _tasks.Remove(done);
            if (_tasks.Count != 0 || _disposed) return;
            foreach (var retired in _retired) retired.Dispose();
            _retired.Clear();
        }), TaskScheduler.Default);
        async Task Run() {
            try {
                var apply = await operation(token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed && generation == _generation && !token.IsCancellationRequested) apply(); });
            } catch (OperationCanceledException) { }
            catch (Exception) {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (!_disposed && generation == _generation) { ClearProtected(); SetNotice("Couldn't load pull request context. Retry when the server is reachable."); }
                });
            } finally {
                await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed && generation == _generation) { settled(); Notify(); } });
            }
        }
    }
    void SetNotice(string value) { _notice = value; Notify(); }
    string? PrLink(string? url, PullRequestSubjectDto subject) => _readers is not null ? _readers.PrLink(url, subject)
        : PullRequestWire.IsGitHub(subject) ? PullRequestWire.PrLink(url, subject) : PullRequestWire.SafeLink(url);
    public async Task TeardownAsync() {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose(); _subscriptions.Dispose(); _cancel.Cancel();
        try { await Task.WhenAll(_tasks.ToArray()); } catch (OperationCanceledException) { }
        _cancel.Dispose(); foreach (var retired in _retired) retired.Dispose();
        _tasks.Clear(); _retired.Clear(); _positions.Clear(); _choices.Clear(); ClearProtected();
        _hasPullRequest.Dispose();
        _notified.Clear();
    }
}
