using Capacitor.Cli.Core;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    bool _refreshDiscovery;
    bool _refreshPage;
    string? _headRestartSection;
    PullRequestSectionState? CurrentSection => _sections.GetValueOrDefault(SectionKey);

    void RequestRefresh(bool manual = false) {
        if (_disposed || !_foreground || _session is not { } session) return;
        if (manual) { _source.ResetSession(session); _stopped = false; _refreshDiscovery = true; _refreshPage = true; _lastOverview = null; }
        if (_stopped || _retryAt > _time.GetUtcNow().UtcDateTime) return;
        // A click waits out a short gap only; the poll keeps the longer one.
        if (_refreshing || _lastRefresh is { } last && _time.GetElapsedTime(last).TotalSeconds < (manual || _queuedRefresh ? 3 : 15)) {
            _queuedRefresh |= manual;
            if (manual) Notify();
            return;
        }
        var refresh = _refreshDiscovery;
        _refreshDiscovery = false;
        _queuedRefresh = false;
        _refreshing = true;
        _lastRefresh = _time.GetTimestamp();
        Notify();
        var repository = _primaryRepo?.Invoke();
        // The daemon reports the branch the worktree was cut on; a session that switched since
        // opens its PR on the new one, which only the checkout's HEAD knows.
        var branch = _branch = GitRepository.CurrentBranch(_worktree) ?? _branch;
        Start(async ct => {
            _readers?.DescribeSession(session, repository, branch);
            var capability = await _source.DiscoverAsync(refresh, ct).ConfigureAwait(false);
            var links = capability.Kind switch {
                PullRequestCapabilityKind.Supported => await _source.ListAsync(session, ct).ConfigureAwait(false),
                PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await _source.LegacyLinksAsync(session, ct).ConfigureAwait(false),
                _ => null
            };
            return () => {
                _refreshing = false;
                _legacy = capability.Kind is PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported;
                if (links is null) {
                    _retryAt = capability.RetryAt;
                    if (capability.Kind == PullRequestCapabilityKind.SignedOut) ForgetChoices();
                    ClearProtected();
                    SetNotice(capability.Kind == PullRequestCapabilityKind.SignedOut ? "Sign in to see pull requests."
                        : "Couldn't discover pull request support. Retry when the server is reachable.");
                    return;
                }
                if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                    if (links.Kind is PullRequestReadKind.SubjectUnavailable or PullRequestReadKind.SignedOut || links.AccessFailure is "invalid" or "denied") {
                        ForgetChoices(); ClearProtected();
                        _stopped = links.Reason == "retries_stopped";
                    } else EnterGrace();
                    SetNotice(_stopped ? "This session's pull requests are unavailable. Use Retry to check again."
                        : links.Kind == PullRequestReadKind.SignedOut ? "Sign in to see pull requests." : "Couldn't refresh the linked pull requests.");
                    return;
                }
                _retryAt = null;
                _pollAfter = Math.Max(15, links.PollAfterSeconds);
                _sessionItems.Clear();
                _sessionItems.AddRange(links.Data.Items);
                _hasListed = true;
                ApplyChoices(listed: true);
            };
        }, () => _refreshing = false);
    }

    void RequestOverview() {
        if (_disposed || !_foreground || _legacy || _stopped || _overviewPending || _session is not { } session || _selected is not { IsAvailable: true } choice
            || _retryAt > _time.GetUtcNow().UtcDateTime || _lastOverview is { } last && _time.GetElapsedTime(last).TotalSeconds < 15) return;
        _overviewPending = true;
        _lastOverview = _time.GetTimestamp();
        Notify();
        Start(async ct => {
            var read = await _source.OverviewAsync(session, choice.Subject, ct).ConfigureAwait(false);
            return () => {
                if (read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale && read.Data is not null && AcceptAccess(read)) {
                    // Checks belong to a commit: a new head drops the old rows, and the open tab reloads below.
                    if (_overview?.HeadSha is { } old && old != read.Data.HeadSha) _sections.Remove("checks");
                    var rollupMoved = _overview?.Checks?.Rollup != read.Data.Checks?.Rollup;
                    _overview = read.Data; _overviewRead = read;
                    SetNotice(read.Kind == PullRequestReadKind.Stale ? "Showing an earlier snapshot while GitHub is unavailable." : "");
                    if (Learn(choice.Subject, read.Data.Lifecycle) && ReconsiderDefault()) return;
                    // Check rows are a snapshot while the summary tracks the overview: reload them while
                    // any is still running, or the summary goes green beside rows that still say pending.
                    var checksLive = _section == "checks" && CurrentSection is { } checks
                        && (rollupMoved || checks.Pages.Any(page => page.Rows.Any(row => row.Outcome == "pending")));
                    // The open tab follows the poll too, unless the reader paged past the first page:
                    // a reload restarts at page one and would pull the rows they were reading away.
                    var onePage = CurrentSection is { Pages.Count: 1, Earlier.Count: 0 };
                    var stoppedChecks = _section == "checks" && CurrentSection is { Stopped: true };
                    var reload = _refreshPage || checksLive || onePage || stoppedChecks;
                    if (_readerVisible && _section != "overview" && (CurrentSection is null || reload)) RequestPage(null, refresh: reload);
                    _refreshPage = false;
                } else Fail(read);
            };
        }, () => _overviewPending = false);
    }

    void RequestPage(string? cursor, bool refresh = false, bool earlier = false) {
        if (_disposed || !CanReveal || _session is not { } session || _selected is not { } choice || _section == "overview"
            || _retryAt > _time.GetUtcNow().UtcDateTime) return;
        var key = SectionKey;
        if (_pageRequests.Contains(key)) return;
        if (!refresh && CurrentSection is { Stopped: true }) return;
        var section = _section;
        var filter = section == "threads" ? _resolved : null;
        var thread = _thread;
        _pageRequests.Add(key);
        Notify();
        // Set by a head_changed restart; the overview it asks for goes out once this read has settled,
        // or its apply would find this request still in flight and skip the reload.
        var restartOverview = false;
        switch (section) {
            case "checks": Page<PullRequestCheckDto>(ToRow); break;
            case "reviewers": Page<PullRequestReviewerDto>(ToRow); break;
            case "reviews": Page<PullRequestReviewDto>(ToRow); break;
            case "threads": Page<PullRequestThreadDto>(ToRow); break;
            default: Page<PullRequestCommentDto>(ToRow); break;
        }
        void Page<T>(Func<T, PullRequestSubjectDto, PullRequestRow> project) where T : class => Start(async ct => {
            var read = await _source.PageAsync<T>(session, choice.Subject, section, cursor, filter, thread, ct).ConfigureAwait(false);
            return () => {
                if (read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale && read.Data is { } page && AcceptAccess(read)) {
                    var state = _sections.GetValueOrDefault(key) ?? new PullRequestSectionState(key);
                    if (state.Snapshot is not null && state.Snapshot != page.SnapshotId && cursor is not null) { FailProtocol(); return; }
                    if (refresh || state.Snapshot != page.SnapshotId) { state.Pages.Clear(); state.Earlier.Clear(); }
                    var rows = page.Items.Select(item => project(item, choice.Subject)).ToArray();
                    var existing = state.Pages.FindIndex(item => item.Cursor == page.PageCursor);
                    var saved = new PullRequestSectionState.Page(page.PageCursor, page.NextCursor, rows, _time.GetTimestamp());
                    if (existing >= 0) state.Pages[existing] = saved;
                    else if (earlier) { state.Pages.Insert(0, saved); state.Earlier.Remove(page.PageCursor); }
                    else {
                        var known = state.Pages.SelectMany(p => p.Rows).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                        if (rows.Any(row => !known.Add(row.Id))) { FailProtocol(); return; }
                        state.Pages.Add(saved);
                    }
                    if (_headRestartSection == key) _headRestartSection = null;
                    state.Snapshot = page.SnapshotId; state.Completed = page.SnapshotCompletedAt;
                    state.Head = page.HeadSha; state.Coverage = page.Coverage;
                    state.Total = page.Total; state.Excluded = page.ExcludedByFilter; state.Stopped = false; state.Error = null;
                    _sections[key] = state;
                    EnforcePageBudget(state, saved, earlier);
                    state.Next = state.Pages.LastOrDefault()?.Next;
                    SetNotice(read.Kind == PullRequestReadKind.Stale ? "Showing an earlier page while GitHub is unavailable." : "");
                } else if (read.Kind == PullRequestReadKind.Restart && read.Reason == "head_changed" && _headRestartSection is null) {
                    // Learn the new head from a fresh overview, whose apply reloads this section. Once
                    // only for that section: an overview still on the old head would bounce straight back here.
                    _headRestartSection = key;
                    _sections.Remove(key);
                    _lastOverview = null;
                    restartOverview = true;
                } else if (read.Kind == PullRequestReadKind.Restart && read.Reason is not ("identity_changed" or "integration_changed")) {
                    var state = _sections.GetValueOrDefault(key) ?? new PullRequestSectionState(key);
                    state.Stopped = true; state.Error = read.Reason == "head_changed" ? "Catching up with a new commit." : "This snapshot can no longer load pages. Refresh to start again.";
                    if (read.Reason == "head_changed") state.Pages.Clear();
                    _sections[key] = state;
                    Notify();
                } else Fail(read);
            };
        }, () => { _pageRequests.Remove(key); if (restartOverview) RequestOverview(); });
    }
    void EnforcePageBudget(PullRequestSectionState state, PullRequestSectionState.Page newest, bool earlier) {
        while (state.Pages.Count > 8) {
            var evict = earlier ? state.Pages.Last(page => !ReferenceEquals(page, newest)) : state.Pages.First(page => !ReferenceEquals(page, newest));
            Evict(state, evict, rememberEarlier: !earlier);
        }
        while (_sections.Values.Sum(section => section.Bytes) + 2L * (_overview?.Description?.Length ?? 0) > 32 * 1024 * 1024) {
            var empty = _sections.Values.FirstOrDefault(section => section.Key != SectionKey && section.Pages.Count == 0);
            if (empty is not null) { _sections.Remove(empty.Key); continue; }
            var candidates = _sections.Values.Where(section => section.Pages.Count > 0)
                .Select(section => (Section: section, Page: section.Pages[0]))
                .Where(item => !ReferenceEquals(item.Page, newest)).OrderBy(item => item.Section.Key == SectionKey).ThenBy(item => item.Page.Touched).ToArray();
            if (candidates.Length == 0) break;
            var evict = candidates[0]; Evict(evict.Section, evict.Page, rememberEarlier: true);
        }
    }
    static void Evict(PullRequestSectionState section, PullRequestSectionState.Page page, bool rememberEarlier) {
        section.Pages.Remove(page);
        if (rememberEarlier) {
            if (section.Earlier.Count >= 5000) section.Earlier.RemoveAt(0);
            section.Earlier.Add(page.Cursor);
        }
    }
    void Fail<T>(PullRequestRead<T> read) where T : class {
        _retryAt = read.RetryAt;
        // A superseded provider, a changed identity or a lost reader outlives its own request: cancel the
        // in-flight reads it left behind and advance the generation, or a stale completion would still land.
        if (read.Kind == PullRequestReadKind.Restart && read.Reason is "identity_changed" or "integration_changed" || read.Reason == "no_reader") { CancelReads(); ClearProtected(); }
        else if (read.AccessFailure == "transient" || read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale
            || read.AccessFailure is null && read.Reason is "timeout" or "provider_unavailable" or "rate_limited" or "budget_exhausted" or "capacity_exhausted") EnterGrace();
        else ClearProtected();
        SetNotice(Reason(read));
    }
    void ApplyChoices(bool listed) {
        var incoming = _sessionItems.Select(link => new PullRequestChoice(link)).ToList();
        var previous = _selected?.Subject;
        var selected = _explicitSelection ? incoming.FirstOrDefault(choice => choice.Subject == previous) : null;
        if (_explicitSelection && selected is null && _selected is not null) {
            selected = _selected with { IsAvailable = false };
            incoming.Insert(0, selected);
        }
        selected ??= DefaultChoice(incoming);
        _updatingChoices = true;
        try {
            if (!_choices.SequenceEqual(incoming)) { _choices.Clear(); _choices.AddRange(incoming); }
        } finally { _updatingChoices = false; }
        if (previous != selected?.Subject) Select(selected);
        else { _selected = selected; this.RaisePropertyChanged(nameof(Selected)); }
        if (listed) {
            if (selected is { IsAvailable: false }) {
                CancelReads(); ClearProtected(); SetNotice("This pull request is no longer linked or visible. Select another PR or retry.");
            }
            else if (_legacy) { ClearProtected(); SetNotice("Open on GitHub. Native PR reading requires a compatible server and app."); }
            else if (selected is null) { ClearProtected(); SetNotice(""); }
            else RequestOverview();
        }
        Notify();
    }
    /// The PR a user most likely wants without asking: one not known to be merged or closed, in
    /// the primary repository, on the checked-out branch, the newest. An unknown lifecycle ranks
    /// as open until its overview says otherwise.
    PullRequestChoice? DefaultChoice(IEnumerable<PullRequestChoice> choices) {
        var primary = _primaryRepo?.Invoke()?.RepoHash;
        return choices
            .Where(choice => choice.IsAvailable)
            .OrderBy(IsSettled)
            .ThenByDescending(choice => primary is not null && choice.Link.RepoHash == primary)
            .ThenByDescending(choice => _branch is not null && choice.Link.HeadRef == _branch)
            .ThenByDescending(choice => choice.Link.Number)
            .FirstOrDefault();
    }
    /// An overview the card has read is fresher than the list row it was chosen from, which waits
    /// for the next list refresh.
    bool IsSettled(PullRequestChoice choice) =>
        (_lifecycles.GetValueOrDefault(choice.Subject) ?? choice.Link.Lifecycle) is "merged" or "closed";
    /// True when the subject's lifecycle is new or has changed.
    bool Learn(PullRequestSubjectDto subject, string? lifecycle) {
        if (lifecycle is null) return false;
        var changed = _lifecycles.GetValueOrDefault(subject) != lifecycle;
        _lifecycles[subject] = lifecycle;
        return changed;
    }
    /// A default whose overview says merged hands over to the next candidate; an explicit choice
    /// is never moved.
    bool ReconsiderDefault() {
        if (_explicitSelection) return false;
        var best = DefaultChoice(_choices);
        if (best is null || best.Subject == _selected?.Subject) return false;
        Select(best);
        return true;
    }
    void ForgetChoices() { CancelReads(); _choices.Clear(); _sessionItems.Clear(); _selected = null; }
    void FailProtocol() { ClearProtected(); SetNotice("The server returned an inconsistent PR response. Retry after updating the server and app."); }
}
