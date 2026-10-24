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
        if (_stopped || HostRateLimitRetryAt > _time.GetUtcNow().UtcDateTime || _listRetryAt > _time.GetUtcNow().UtcDateTime) return;
        // A click waits out a short gap only; the poll keeps the longer one.
        if (_refreshing || _lastRefresh is { } last && _time.GetElapsedTime(last).TotalSeconds < (manual || _queuedRefresh ? 3 : 15)) {
            _queuedRefresh |= manual;
            if (manual) Notify();
            return;
        }
        // A fresh manual discovery supersedes overview/page reads using the old provider state.
        if (manual) CancelReads();
        var refresh = _refreshDiscovery;
        _refreshDiscovery = false;
        _queuedRefresh = false;
        _refreshing = true;
        _lastRefresh = _time.GetTimestamp();
        Notify();
        var worktree = _worktree;
        var reportedBranch = _branch;
        var generation = _generation;
        var skipBranchLookup = worktree is null && !_hasCustomBranchReader;
        var cacheGeneration = _snapshots?.Generation ?? 0;
        if (skipBranchLookup) _readers?.DescribeSession(session, _primaryRepo?.Invoke(), reportedBranch);
        Start(async ct => {
            var branch = reportedBranch;
            if (!skipBranchLookup) {
                var resolved = await Task.Run(() => _readBranch(worktree), ct).WaitAsync(ct).ConfigureAwait(false);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                    ct.ThrowIfCancellationRequested();
                    if (_disposed || generation != _generation) throw new OperationCanceledException(ct);
                    // A reported branch can advance while the filesystem lookup is in flight.
                    branch = _worktree == worktree && _branch == reportedBranch ? resolved ?? reportedBranch : _branch;
                    _readers?.DescribeSession(session, _primaryRepo?.Invoke(), branch);
                });
            }
            var capability = await _source.DiscoverAsync(refresh, ct).ConfigureAwait(false);
            var links = capability.Kind switch {
                PullRequestCapabilityKind.Supported => await _source.ListAsync(session, ct).ConfigureAwait(false),
                PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await _source.LegacyLinksAsync(session, ct).ConfigureAwait(false),
                _ => null
            };
            return () => {
                if (_worktree == worktree && _branch == reportedBranch) _branch = branch;
                _refreshing = false;
                _legacy = capability.Kind is PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported;
                if (links is null) {
                    _listRetryAt = capability.RetryAt;
                    if (capability.Kind == PullRequestCapabilityKind.SignedOut) {
                        _snapshots?.Clear();
                        ForgetChoices(); ClearProtected(); _notice = "Sign in to see pull requests.";
                    } else {
                        if (!CanReveal && !_overviewPending) EnterGrace("Couldn't discover pull request support. Retry when the server is reachable.");
                        if (!CanDisplay && !_overviewPending) _notice = "Couldn't discover pull request support. Retry when the server is reachable.";
                    }
                    return;
                }
                if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                    _listRetryAt = links.RetryAt;
                    if (links.Kind is PullRequestReadKind.SubjectUnavailable or PullRequestReadKind.SignedOut || links.AccessFailure is "invalid" or "denied") {
                        if (links.Kind == PullRequestReadKind.SignedOut) _snapshots?.Clear();
                        ForgetChoices(); ClearProtected();
                        _stopped = links.Reason == "retries_stopped";
                        if (_stopped) _queuedRefresh = false;
                        _notice = _stopped ? "This session's pull requests are unavailable. Use Retry to check again."
                            : links.Kind == PullRequestReadKind.SignedOut ? "Sign in to see pull requests." : "Couldn't refresh the linked pull requests.";
                    } else {
                        if (!CanReveal && !_overviewPending) EnterGrace("Couldn't refresh the linked pull requests.");
                        if (!CanDisplay && !_overviewPending) _notice = "Couldn't refresh the linked pull requests.";
                    }
                    return;
                }
                _listRetryAt = null;
                _pollAfter = Math.Max(15, links.PollAfterSeconds);
                var linksChanged = !_hasListed || !_sessionItems.Select(PullRequestWire.Subject).ToHashSet()
                    .SetEquals(links.Data.Items.Select(PullRequestWire.Subject));
                _sessionItems.Clear();
                _sessionItems.AddRange(links.Data.Items);
                _hasListed = true;
                _snapshots?.Set(session, new([.. links.Data.Items], _snapshots.Get(session)?.Overview), cacheGeneration);
                ApplyChoices(listed: true);
                if (linksChanged) _linksChanged?.Invoke(session);
            };
        }, () => _refreshing = false);
    }

    void RequestOverview() {
        if (_disposed || !_foreground || _legacy || _stopped || _overviewPending || _session is not { } session || _selected is not { IsAvailable: true } choice
            || ReadRetryAt > _time.GetUtcNow().UtcDateTime || _lastOverview is { } last && _time.GetElapsedTime(last).TotalSeconds < 15) return;
        _overviewPending = true;
        _lastOverview = _time.GetTimestamp();
        var cacheGeneration = _snapshots?.Generation ?? 0;
        Notify();
        Start(async ct => {
            var read = await _source.OverviewAsync(session, choice.Subject, ct).ConfigureAwait(false);
            return () => {
                if (read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale && read.Data is not null && AcceptAccess(read)) {
                    var headChanged = _overview?.HeadSha is { } old && old != read.Data.HeadSha;
                    var rollupMoved = _overview?.Checks?.Rollup != read.Data.Checks?.Rollup;
                    _overview = read.Data; _overviewRead = read;
                    _snapshots?.Set(session, new([.. _sessionItems], read), cacheGeneration);
                    _notice = "";
                    if (Learn(choice.Subject, read.Data.Lifecycle) && ReconsiderDefault()) return;
                    // Check rows are a snapshot while the summary tracks the overview: reload them while
                    // any is still running, or the summary goes green beside rows that still say pending.
                    var checksLive = _section == "checks" && CurrentSection is { } checks
                        && (headChanged || rollupMoved || checks.Pages.Any(page => page.Rows.Any(row => row.Outcome == "pending")));
                    // The open tab follows the poll too, unless the reader paged past the first page:
                    // a reload restarts at page one and would pull the rows they were reading away.
                    var onePage = CurrentSection is { Pages.Count: 1, Earlier.Count: 0 };
                    var stoppedChecks = _section == "checks" && CurrentSection is { Stopped: true };
                    var reload = _refreshPage || _headRestartSection == SectionKey || checksLive || onePage || stoppedChecks;
                    if (_readerVisible && ReadsRows && (CurrentSection is null || reload)) RequestPage(null, refresh: reload);
                    else if (headChanged) _sections.Remove("checks");
                    _refreshPage = false;
                } else Fail(read);
            };
        }, () => _overviewPending = false);
    }

    void RequestPage(string? cursor, bool refresh = false, bool earlier = false) {
        if (_disposed || !CanReveal || _session is not { } session || _selected is not { } choice || !ReadsRows
            || ReadRetryAt > _time.GetUtcNow().UtcDateTime) return;
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
                    if (state.Snapshot is not null && state.Snapshot != page.SnapshotId && cursor is not null) { FailProtocol(read.Reader); return; }
                    if (refresh || state.Snapshot != page.SnapshotId) { state.Pages.Clear(); state.Earlier.Clear(); }
                    var rows = page.Items.Select(item => project(item, choice.Subject)).ToArray();
                    var existing = state.Pages.FindIndex(item => item.Cursor == page.PageCursor);
                    var saved = new PullRequestSectionState.Page(page.PageCursor, page.NextCursor, rows, _time.GetTimestamp());
                    if (existing >= 0) state.Pages[existing] = saved;
                    else if (earlier) { state.Pages.Insert(0, saved); state.Earlier.Remove(page.PageCursor); }
                    else {
                        var known = state.Pages.SelectMany(p => p.Rows).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                        if (rows.Any(row => !known.Add(row.Id))) { FailProtocol(read.Reader); return; }
                        state.Pages.Add(saved);
                    }
                    if (_headRestartSection == key) _headRestartSection = null;
                    state.Snapshot = page.SnapshotId; state.Completed = page.SnapshotCompletedAt;
                    state.Head = page.HeadSha; state.Coverage = page.Coverage;
                    state.Total = page.Total; state.Excluded = page.ExcludedByFilter; state.Stopped = false; state.Error = null;
                    _sections[key] = state;
                    EnforcePageBudget(state, saved, earlier);
                    state.Next = state.Pages.LastOrDefault()?.Next;
                    _notice = "";
                } else if (read.Kind == PullRequestReadKind.Restart && read.Reason == "head_changed" && _headRestartSection is null) {
                    // Learn the new head from a fresh overview, whose apply reloads this section. Once
                    // only for that section: an overview still on the old head would bounce straight back here.
                    _headRestartSection = key;
                    if (_sections.TryGetValue(key, out var state)) state.Stopped = true;
                    _lastOverview = null;
                    restartOverview = true;
                } else if (read.Kind == PullRequestReadKind.Restart && read.Reason is not ("identity_changed" or "integration_changed")) {
                    var state = _sections.GetValueOrDefault(key) ?? new PullRequestSectionState(key);
                    state.Stopped = true; state.Error = read.Reason == "head_changed" ? "Catching up with a new commit." : "This snapshot can no longer load pages. Refresh to start again.";
                    _sections[key] = state;
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
        RememberRetry(read);
        var reason = Reason(read);
        // Access loss invalidates peer reads too; a late page must not reopen protected content.
        if (read.Kind == PullRequestReadKind.Restart && read.Reason is "identity_changed" or "integration_changed" || read.Reason == "no_reader") { CancelReads(); ClearProtected(); ForgetOverview(); }
        else if (read.AccessFailure == "transient" || read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale
            || read.AccessFailure is null && read.Reason is "timeout" or "provider_unavailable" or "rate_limited" or "budget_exhausted" or "capacity_exhausted") {
            EnterGrace(reason);
            if (_grace) return;
        }
        else { CancelReads(); ClearProtected(); ForgetOverview(); }
        SetNotice(reason);
    }
    /// A session reopened shows its last list at once, and its overview only while that read's
    /// access still stands; the refresh that follows replaces both.
    void Seed(PullRequestSnapshot snapshot) {
        _sessionItems.AddRange(snapshot.Links);
        _hasListed = true;
        ApplyChoices(listed: true);
        ShowLastOverview();
    }
    void ShowLastOverview() {
        if (_session is not { } session || _snapshots?.Get(session)?.Overview is not { Data: { } overview } read || !AcceptAccess(read)) return;
        _overview = overview; _overviewRead = read;
        Notify();
    }
    void ForgetOverview() {
        if (_session is { } session && _snapshots?.Get(session) is { Overview: not null } snapshot)
            _snapshots.Set(session, snapshot with { Overview = null }, _snapshots.Generation);
    }
    void ApplyChoices(bool listed) {
        if (_selected is null) ApplyRequestedSubject();
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
        if (previous != selected?.Subject) Select(selected, explicitSelection: _explicitSelection);
        else { _selected = selected; this.RaisePropertyChanged(nameof(Selected)); }
        if (listed) {
            _requestedSubject = null;
            if (selected is { IsAvailable: false }) {
                CancelReads(); ClearProtected(); SetNotice("This pull request is no longer linked or visible. Select another PR or retry.");
            }
            else if (selected is null) { ClearProtected(); SetNotice(NoPullRequestsNote); }
            else if (_legacy) { ClearProtected(); SetNotice("Open on GitHub. Native PR reading requires a compatible server and app."); }
            else {
                if (_notice == NoPullRequestsNote) _notice = "";
                RequestOverview();
            }
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
    void ForgetChoices() {
        if (_session is { } session) _snapshots?.Forget(session);
        if (_explicitSelection && _selected is not null) _requestedSubject = _selected.Subject;
        CancelReads(); _choices.Clear(); _sessionItems.Clear(); _selected = null;
    }
    void FailProtocol(string? reader) {
        CancelReads();
        ClearProtected();
        ForgetOverview();
        SetNotice(reader is null ? "The server returned an inconsistent PR response. Retry after updating the server and app."
            : $"{reader} returned an inconsistent PR response. Refresh to reload it.");
    }
}
