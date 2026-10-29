using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// The PR tone of every listed session, keyed by session id. A row whose listing carried its PR
/// links is marked from the row and never read here; any other the rail shows is read on a slow
/// cadence through the same links-then-overview path the open workspace uses. A session without a
/// tone is absent rather than mapped to None, so consumers can test membership.
///
/// A read's titles show only within its access window; past it the summary keeps each PR's number
/// and lifecycle, which the tone already reveals. Renewing the window instead would re-read every
/// shown session every twenty seconds, and with the gh reader each read is a process spawn.
public sealed class PullRequestToneCache : IDisposable {
    static readonly TimeSpan DefaultRefreshEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan DefaultEndedRefreshEvery = TimeSpan.FromMinutes(10);
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(1);
    /// Every listed session comes due at once after a connect or a scope switch, and the server
    /// refuses a fifth concurrent PR read from one user.
    const int MaxConcurrentReads = 4;

    /// What the cache holds for one listed session. The version is reissued on every invalidation, so
    /// a read started before it recognises its result as stale.
    sealed class Entry(long version) {
        public long                    Version = version;
        public PullRequestTone?        Tone;
        public RailPullRequestSummary? Summary;
        /// When the summary's reads were taken and how many seconds of access they carried; null
        /// once its titles are dropped.
        public (long Started, double Seconds)? Access;
        public long?                   ReadAt;
        /// Every PR the last read revealed is merged or closed.
        public bool                    Settled;

        public void DropSummary() {
            Summary = null;
            Access  = null;
        }
    }

    readonly IPullRequestSource _source;
    readonly TimeProvider _time;
    readonly TimeSpan _refreshEvery;
    readonly TimeSpan _endedRefreshEvery;
    readonly object _lock = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    /// Outlives a removed entry: a session listed again waits for the read it still has in flight.
    readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    long _nextVersion;
    readonly BehaviorSubject<IReadOnlyDictionary<string, PullRequestTone>> _published = new(FrozenDictionary<string, PullRequestTone>.Empty);
    readonly BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>> _summaryPublished = new(FrozenDictionary<string, RailPullRequestSummary>.Empty);
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _cancel = new();
    readonly ITimer _timer;
    /// The sessions read here; a listed session is never among them.
    IReadOnlySet<string> _sessions = FrozenSet<string>.Empty;
    /// Sessions marked from the PR links their listing row carried.
    IReadOnlyDictionary<string, RepoSessionPullRequests> _listed = FrozenDictionary<string, RepoSessionPullRequests>.Empty;
    /// Sessions no daemon here hosts — ended, a terminal's, a teammate's: none is this app's to poll
    /// every two minutes, and under Team there can be hundreds.
    IReadOnlySet<string> _slowSessions = FrozenSet<string>.Empty;
    /// Ended sessions: once their PRs have all settled, nothing but a workspace refresh reads them again.
    IReadOnlySet<string> _endedSessions = FrozenSet<string>.Empty;
    /// The server's read budget is the user's, not a session's: once it refuses one read, no read
    /// starts before the time it gave.
    DateTime _heldUntil = DateTime.MinValue;
    bool _disposed;

    public PullRequestToneCache(
            IAgentDirectory directory, IPullRequestSource source, TimeProvider time,
            IObservable<Func<AgentRow, bool>>? shown = null,
            TimeSpan? refreshEvery = null, TimeSpan? endedRefreshEvery = null) {
        _source = source;
        _time = time;
        _refreshEvery = refreshEvery ?? DefaultRefreshEvery;
        _endedRefreshEvery = endedRefreshEvery ?? DefaultEndedRefreshEvery;
        directory.Rows.Connect().ToCollection()
            .CombineLatest(directory.LocalDaemonOnAppServer, shown ?? Observable.Return<Func<AgentRow, bool>>(_ => true),
                (rows, localOnAppServer, isShown) => (
                Listed: rows.Where(r => r is { SessionId.Length: > 0, ListedPullRequests: not null })
                    .GroupBy(r => r.SessionId!, StringComparer.Ordinal)
                    .ToFrozenDictionary(g => g.Key, g => g.First().ListedPullRequests!, StringComparer.Ordinal),
                Polled: rows.Where(r => r.SessionId is { Length: > 0 } && Polled(r, localOnAppServer) && isShown(r)).ToList()))
            .Subscribe(next => {
                var rows = next.Polled.Where(r => !next.Listed.ContainsKey(r.SessionId!)).ToList();
                lock (_lock) {
                    _listed = next.Listed;
                    MarkListed();
                    _sessions = rows.Select(r => r.SessionId!).ToFrozenSet(StringComparer.Ordinal);
                    _slowSessions = rows.Where(r => r.Origin == AgentOrigin.Server).Select(r => r.SessionId!)
                        .Except(rows.Where(r => r.Origin != AgentOrigin.Server).Select(r => r.SessionId!))
                        .ToFrozenSet(StringComparer.Ordinal);
                    _endedSessions = rows.Where(r => r.IsEndedSession).Select(r => r.SessionId!)
                        .Except(rows.Where(r => !r.IsEndedSession).Select(r => r.SessionId!))
                        .ToFrozenSet(StringComparer.Ordinal);
                }
                Tick();
            })
            .DisposeWith(_subscriptions);
        _timer = time.CreateTimer(_ => Tick(), null, TickEvery, TickEvery);
    }

    /// A teammate's session is never read here, whichever lane shows it: each read costs the server
    /// a visibility check and a GitHub-backed admission per PR every access window, and under Team
    /// there are hundreds. Its PRs show when it opens. A daemon row the server has not named yet is
    /// presumed the viewer's own, as the directory presumes; a listed row waits for the owners read.
    /// A local row's session id names a session on whatever server the daemon reports; only while
    /// that is the app's own server can it be read here (the directory's own rule).
    static bool Polled(AgentRow row, bool localOnAppServer) => row.Origin switch {
        AgentOrigin.Local => localOnAppServer && row.OwnerIsViewer != false,
        AgentOrigin.Server => row.OwnerIsViewer == true,
        _ => row.OwnerIsViewer != false,
    };

    public IObservable<IReadOnlyDictionary<string, PullRequestTone>> Tones => _published.DistinctUntilChanged(new MapEquality());

    public IReadOnlyDictionary<string, PullRequestTone> Current => _published.Value;

    public IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>> Summaries => _summaryPublished;
    public IReadOnlyDictionary<string, RailPullRequestSummary> CurrentSummaries => _summaryPublished.Value;

    /// A workspace discovered new links; recheck this session without waiting for the background poll.
    public void Refresh(string session) {
        lock (_lock) {
            if (_disposed || !_sessions.Contains(session)) return;
            // An older in-flight list must not publish after the workspace's newer discovery.
            var entry = EntryFor(session);
            entry.Version = ++_nextVersion;
            entry.ReadAt  = null;
        }
        Tick();
    }

    Entry EntryFor(string session) {
        if (!_entries.TryGetValue(session, out var entry)) _entries[session] = entry = new Entry(++_nextVersion);

        return entry;
    }

    TimeSpan CadenceFor(string session) => _slowSessions.Contains(session) ? _endedRefreshEvery : _refreshEvery;

    bool Due(string session, Entry entry) => entry.ReadAt is not { } at
        || (!(entry.Settled && _endedSessions.Contains(session)) && _time.GetElapsedTime(at) >= CadenceFor(session));

    /// The entry a read started at <paramref name="version"/> may still write to, or null when that read is stale.
    Entry? LiveEntry(string session, long version) =>
        !_disposed && _entries.TryGetValue(session, out var entry) && entry.Version == version ? entry : null;

    int _ticking;
    bool _tickAgain;

    /// One drain at a time, iterative: a read that finishes without awaiting calls back in from its
    /// own stack, and a nested drain per due session would grow with the listing. The caller that
    /// finds a drain running leaves a note the running one honours before it returns.
    void Tick() {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) {
            Volatile.Write(ref _tickAgain, true);
            return;
        }
        try {
            do {
                Volatile.Write(ref _tickAgain, false);
                Drain();
            } while (Volatile.Read(ref _tickAgain));
        } finally {
            Volatile.Write(ref _ticking, 0);
        }
        // A note left after the loop read it, by a caller that arrived between the check and the reset.
        if (Volatile.Read(ref _tickAgain)) Tick();
    }

    void Drain() {
        List<(string Session, long Version)> due;
        CancellationToken ct;
        lock (_lock) {
            if (_disposed) return;
            // Taken under the lock: a dispose racing the reads below would otherwise hand them a disposed source.
            ct = _cancel.Token;
            var tonesChanged = false;
            var summariesChanged = false;
            foreach (var session in _entries.Keys.Where(s => !_sessions.Contains(s) && !_listed.ContainsKey(s)).ToList()) {
                var gone = _entries[session];
                tonesChanged     |= gone.Tone is not null;
                summariesChanged |= gone.Summary is not null;
                _entries.Remove(session);
            }
            foreach (var session in _sessions) EntryFor(session);
            foreach (var entry in _entries.Values.Where(e => e.Access is { } access
                         && _time.GetElapsedTime(access.Started).TotalSeconds > access.Seconds - 5)) {
                entry.Access = null;
                entry.Summary = entry.Summary is { } summary
                    ? new(summary.MenuItems.Select(item => item with { Title = null }).ToArray()) : null;
                summariesChanged = true;
            }
            if (tonesChanged) Publish();
            if (summariesChanged) PublishSummaries();
            due = _time.GetUtcNow().UtcDateTime < _heldUntil ? [] : _sessions
                .Select(s => (Session: s, Entry: _entries[s]))
                .Where(e => !_inFlight.Contains(e.Session) && Due(e.Session, e.Entry))
                .Take(Math.Max(0, MaxConcurrentReads - _inFlight.Count))
                .Select(e => (e.Session, e.Entry.Version)).ToList();
            foreach (var (session, _) in due) {
                _inFlight.Add(session);
                _entries[session].ReadAt = _time.GetTimestamp();
            }
        }
        foreach (var (session, version) in due) _ = ReadAsync(session, version, ct);
    }

    /// Under the lock. The listing's record has no access window, so its mark stands until the row
    /// changes; a read still in flight for a session that became listed publishes nothing.
    void MarkListed() {
        var changed = false;
        foreach (var (session, listed) in _listed) {
            var entry = EntryFor(session);
            if (_inFlight.Contains(session)) entry.Version = ++_nextVersion;
            var items = (listed.Items ?? [])
                .Select(pr => (Subject: ListedPullRequest.Subject(pr), PullRequest: pr))
                .Where(pr => PullRequestWire.ValidSubject(pr.Subject))
                .DistinctBy(pr => pr.Subject).ToList();
            var tone = PullRequestTones.Strongest(items.Select(pr => ListedTone(pr.PullRequest.State)));
            RailPullRequestSummary? summary = items.Count == 0 ? null : new(items.Select(pr => new RailPullRequestMenuItem(
                pr.Subject, pr.PullRequest.Title, LifecycleLabel(pr.PullRequest.State))).ToArray());
            var nextTone = tone == PullRequestTone.None ? null : (PullRequestTone?)tone;
            if (entry.Access is not null || entry.Tone != nextTone
                || (entry.Summary is null ? summary is not null : summary is null || !SameSummary(entry.Summary, summary))) {
                entry.Version = ++_nextVersion;
                entry.Tone = nextTone;
                entry.DropSummary();
                entry.Summary = summary;
                changed = true;
            }
        }
        if (!changed) return;
        Publish();
        PublishSummaries();
    }

    /// The listing carries no checks or mergeability, so an open PR reads as an overview without them does.
    static PullRequestTone ListedTone(string? state) => state switch {
        "merged" => PullRequestTone.Merged,
        "closed" => PullRequestTone.Closed,
        "draft" => PullRequestTone.Draft,
        "open" => PullRequestTone.Ready,
        _ => PullRequestTone.None,
    };

    /// Signed out, a lost subject, a refused or invalid access window, or a reader that changed under the
    /// read: the session's PRs can no longer be shown, as opposed to a read that merely failed this time.
    static bool Revokes<T>(PullRequestRead<T> read) where T : class =>
        read.Kind is PullRequestReadKind.SignedOut or PullRequestReadKind.SubjectUnavailable
        || read.AccessFailure is "denied" or "invalid"
        || read.Reason is "identity_changed" or "integration_changed" or "no_reader";

    static DateTime? Refused<T>(PullRequestRead<T> read) where T : class => read.StatusCode == 429 ? read.RetryAt : null;

    /// The refused read keeps the last tone, as any miss does, and goes again once the hold lapses
    /// rather than on its session's cadence.
    void Hold(string session, long version, DateTime until) {
        lock (_lock) {
            // A zero Retry-After, or a date already past, would otherwise re-send the refused read at once.
            var floor = _time.GetUtcNow().UtcDateTime + MinimumHold;
            if (until < floor) until = floor;
            if (until > _heldUntil) _heldUntil = until;
            if (LiveEntry(session, version) is { } entry) entry.ReadAt = null;
        }
    }

    /// A read in flight when another session was refused sends nothing more until the hold lapses,
    /// and goes again whole after it.
    bool Held(string session, long version) {
        lock (_lock) {
            if (_time.GetUtcNow().UtcDateTime >= _heldUntil) return false;
            if (LiveEntry(session, version) is { } entry) entry.ReadAt = null;
            return true;
        }
    }

    async Task ReadAsync(string session, long version, CancellationToken ct) {
        try {
            var capability = await _source.DiscoverAsync(false, ct).ConfigureAwait(false);
            if (capability.Kind != PullRequestCapabilityKind.Supported) {
                // Signed out, or a server without overviews, is a verdict; an unreachable one is not.
                if (capability.Kind != PullRequestCapabilityKind.Unavailable) Clear(session, version);
                return;
            }
            if (Held(session, version)) return;
            var links = await _source.ListAsync(session, ct).ConfigureAwait(false);
            if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                if (Refused(links) is { } until) Hold(session, version, until);
                else if (Revokes(links)) Clear(session, version);
                return;
            }
            var revealed = new List<(PullRequestLinkDto Link, PullRequestRead<PullRequestOverviewDto> Read)>();
            var denied = false;
            var missed = false;
            foreach (var link in links.Data.Items.DistinctBy(PullRequestWire.Subject)) {
                // A denial already read outranks the PRs this read cannot reach.
                if (Held(session, version)) {
                    if (denied) Clear(session, version);
                    return;
                }
                var read = await _source.OverviewAsync(session, PullRequestWire.Subject(link), ct).ConfigureAwait(false);
                if (Refused(read) is { } until) {
                    if (denied) Clear(session, version);
                    Hold(session, version, until);
                    return;
                }
                // The same gate the reader applies: a read past its access window reveals nothing.
                if (read.CanReveal(_time)) revealed.Add((link, read));
                else if (Revokes(read)) denied = true;
                else missed = true;
            }
            // A denial clears the tone, as the card does, whatever the session's other PRs read. A
            // miss on any PR keeps the last tone whole: the readable remainder alone could only
            // understate it.
            if (denied) Clear(session, version);
            else if (!missed) Settle(session, version, revealed);
        } catch (OperationCanceledException) {
        } finally {
            lock (_lock) _inFlight.Remove(session);
            // A refresh may have invalidated this read while it was in flight, and a read held back
            // for a free slot is due now.
            Tick();
        }
    }

    /// Every PR of the session read: its tone, and a summary held for as long as all of them stay revealable.
    void Settle(string session, long version, List<(PullRequestLinkDto Link, PullRequestRead<PullRequestOverviewDto> Read)> revealed) {
        lock (_lock) {
            if (LiveEntry(session, version) is not { } entry) return;
            var tone = PullRequestTones.Strongest(revealed.Select(r => PullRequestTones.From(r.Read.Data!)));
            entry.Tone = tone == PullRequestTone.None ? null : tone;
            entry.Settled = revealed.Count > 0 && revealed.All(r => r.Read.Data!.Lifecycle is "merged" or "closed");
            Publish();
            if (revealed.Count == 0) {
                entry.Summary = null;
                entry.Access  = null;
            } else if (revealed.All(r => r.Read.CanReveal(_time))) {
                var summary = new RailPullRequestSummary(revealed.Select(r => new RailPullRequestMenuItem(
                    PullRequestWire.Subject(r.Link), r.Read.Data!.Title ?? r.Link.Title, LifecycleLabel(r.Read.Data!.Lifecycle, r.Read.Data!.IsDraft))).ToArray());
                if (entry.Summary is not { } previous || !SameSummary(previous, summary)) entry.Summary = summary;
                entry.Access = (_time.GetTimestamp(), revealed.Min(r => r.Read.RemainingSeconds(_time)));
            }
            PublishSummaries();
        }
    }

    static string LifecycleLabel(string? lifecycle, bool? isDraft = null) => lifecycle switch {
        "merged" => "Merged",
        "closed" => "Closed",
        "draft" => "Draft",
        "open" when isDraft == true => "Draft",
        "open" => "Open",
        _ => "Unknown",
    };

    void Clear(string session, long version) {
        lock (_lock) {
            if (LiveEntry(session, version) is not { } entry) return;
            entry.Tone = null;
            entry.Settled = false;
            entry.DropSummary();
            Publish();
            PublishSummaries();
        }
    }

    void PublishSummaries() {
        var summaries = _entries.Where(e => e.Value.Summary is not null).ToDictionary(e => e.Key, e => e.Value.Summary!, StringComparer.Ordinal);
        if (_summaryPublished.Value.Count == summaries.Count
            && _summaryPublished.Value.All(p => summaries.GetValueOrDefault(p.Key) is { } next && SameSummary(next, p.Value))) return;
        _summaryPublished.OnNext(summaries.Count == 0 ? FrozenDictionary<string, RailPullRequestSummary>.Empty
            : summaries.ToFrozenDictionary(StringComparer.Ordinal));
    }

    static bool SameSummary(RailPullRequestSummary left, RailPullRequestSummary right) =>
        left.MenuItems.SequenceEqual(right.MenuItems);

    void Publish() {
        var tones = _entries.Where(e => e.Value.Tone is not null).ToArray();
        _published.OnNext(tones.Length == 0
            ? FrozenDictionary<string, PullRequestTone>.Empty
            : tones.ToFrozenDictionary(e => e.Key, e => e.Value.Tone!.Value, StringComparer.Ordinal));
    }

    sealed class MapEquality : IEqualityComparer<IReadOnlyDictionary<string, PullRequestTone>> {
        public bool Equals(IReadOnlyDictionary<string, PullRequestTone>? x, IReadOnlyDictionary<string, PullRequestTone>? y) =>
            x is not null && y is not null && x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
        public int GetHashCode(IReadOnlyDictionary<string, PullRequestTone> obj) => obj.Count;
    }

    public void Dispose() {
        lock (_lock) { _disposed = true; _cancel.Cancel(); }
        _timer.Dispose();
        _subscriptions.Dispose();
        _published.OnCompleted();
        _summaryPublished.OnCompleted();
        _cancel.Dispose();
    }
}
