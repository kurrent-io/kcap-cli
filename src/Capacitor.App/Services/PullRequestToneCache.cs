using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;

namespace Capacitor.App.Services;

/// The PR tone of every listed session, keyed by session id, read on a slow cadence through the
/// same links-then-overview path the open workspace uses. A session without a tone is absent
/// rather than mapped to None, so consumers can test membership.
public sealed class PullRequestToneCache : IDisposable {
    static readonly TimeSpan DefaultRefreshEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    /// What the cache holds for one listed session. The version is reissued on every invalidation, so
    /// a read started before it recognises its result as stale.
    sealed class Entry(long version) {
        public long                    Version = version;
        public PullRequestTone?        Tone;
        public RailPullRequestSummary? Summary;
        /// When the summary's reads were taken and how many seconds of access they carried.
        public (long Started, double Seconds)? Access;
        public long?                   ReadAt;
        public TimeSpan?               RefreshAfter;

        public void DropSummary() {
            Summary      = null;
            Access       = null;
            RefreshAfter = null;
        }
    }

    readonly IPullRequestSource _source;
    readonly TimeProvider _time;
    readonly TimeSpan _refreshEvery;
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
    IReadOnlySet<string> _sessions = FrozenSet<string>.Empty;
    bool _disposed;

    public PullRequestToneCache(IAgentDirectory directory, IPullRequestSource source, TimeProvider time, TimeSpan? refreshEvery = null) {
        _source = source;
        _time = time;
        _refreshEvery = refreshEvery ?? DefaultRefreshEvery;
        // A local row's session id names a session on whatever server the daemon reports; only
        // while that is the app's own server can it be read here (the directory's own rule).
        directory.Rows.Connect().ToCollection()
            .CombineLatest(directory.LocalDaemonOnAppServer, (rows, localOnAppServer) => rows
                .Where(r => r.SessionId is { Length: > 0 } && (localOnAppServer || r.Origin != AgentOrigin.Local))
                .Select(r => r.SessionId!).ToFrozenSet(StringComparer.Ordinal))
            .Subscribe(sessions => {
                lock (_lock) _sessions = sessions;
                Tick();
            })
            .DisposeWith(_subscriptions);
        _timer = time.CreateTimer(_ => Tick(), null, TickEvery, TickEvery);
    }

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
            entry.Version      = ++_nextVersion;
            entry.ReadAt       = null;
            entry.RefreshAfter = null;
        }
        Tick();
    }

    Entry EntryFor(string session) {
        if (!_entries.TryGetValue(session, out var entry)) _entries[session] = entry = new Entry(++_nextVersion);

        return entry;
    }

    /// The entry a read started at <paramref name="version"/> may still write to, or null when that read is stale.
    Entry? LiveEntry(string session, long version) =>
        !_disposed && _entries.TryGetValue(session, out var entry) && entry.Version == version ? entry : null;

    void Tick() {
        List<(string Session, long Version)> due;
        CancellationToken ct;
        lock (_lock) {
            if (_disposed) return;
            // Taken under the lock: a dispose racing the reads below would otherwise hand them a disposed source.
            ct = _cancel.Token;
            var tonesChanged = false;
            var summariesChanged = false;
            foreach (var session in _entries.Keys.Where(s => !_sessions.Contains(s)).ToList()) {
                var gone = _entries[session];
                tonesChanged     |= gone.Tone is not null;
                summariesChanged |= gone.Summary is not null;
                _entries.Remove(session);
            }
            foreach (var session in _sessions) EntryFor(session);
            foreach (var entry in _entries.Values.Where(e => e.Access is { } access
                         && _time.GetElapsedTime(access.Started).TotalSeconds > access.Seconds - 5)) {
                entry.DropSummary();
                summariesChanged = true;
            }
            if (tonesChanged) Publish();
            if (summariesChanged) PublishSummaries();
            due = _sessions
                .Select(s => (Session: s, Entry: _entries[s]))
                .Where(e => !_inFlight.Contains(e.Session)
                    && (e.Entry.ReadAt is not { } at || _time.GetElapsedTime(at) >= (e.Entry.RefreshAfter ?? _refreshEvery)))
                .Select(e => (e.Session, e.Entry.Version)).ToList();
            foreach (var (session, _) in due) {
                _inFlight.Add(session);
                _entries[session].ReadAt = _time.GetTimestamp();
            }
        }
        foreach (var (session, version) in due) _ = ReadAsync(session, version, ct);
    }

    /// Signed out, a lost subject, a refused or invalid access window, or a reader that changed under the
    /// read: the session's PRs can no longer be shown, as opposed to a read that merely failed this time.
    static bool Revokes<T>(PullRequestRead<T> read) where T : class =>
        read.Kind is PullRequestReadKind.SignedOut or PullRequestReadKind.SubjectUnavailable
        || read.AccessFailure is "denied" or "invalid"
        || read.Reason is "identity_changed" or "integration_changed" or "no_reader";

    async Task ReadAsync(string session, long version, CancellationToken ct) {
        try {
            // Only a successfully renewed summary earns the faster access-window cadence.
            lock (_lock) {
                if (LiveEntry(session, version) is not { } entry) return;
                entry.RefreshAfter = null;
            }
            var capability = await _source.DiscoverAsync(false, ct).ConfigureAwait(false);
            if (capability.Kind != PullRequestCapabilityKind.Supported) {
                // Signed out, or a server without overviews, is a verdict; an unreachable one is not.
                if (capability.Kind != PullRequestCapabilityKind.Unavailable) Clear(session, version);
                return;
            }
            var links = await _source.ListAsync(session, ct).ConfigureAwait(false);
            if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                if (Revokes(links)) Clear(session, version);
                return;
            }
            var revealed = new List<(PullRequestLinkDto Link, PullRequestRead<PullRequestOverviewDto> Read)>();
            var denied = false;
            var missed = false;
            foreach (var link in links.Data.Items.DistinctBy(PullRequestWire.Subject)) {
                var read = await _source.OverviewAsync(session, PullRequestWire.Subject(link), ct).ConfigureAwait(false);
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
            bool refresh;
            lock (_lock) {
                _inFlight.Remove(session);
                refresh = !_entries.TryGetValue(session, out var entry) || entry.ReadAt is null;
            }
            // Refresh may have invalidated this read while it was in flight.
            if (refresh) Tick();
        }
    }

    /// Every PR of the session read: its tone, and a summary held for as long as all of them stay revealable.
    void Settle(string session, long version, List<(PullRequestLinkDto Link, PullRequestRead<PullRequestOverviewDto> Read)> revealed) {
        lock (_lock) {
            if (LiveEntry(session, version) is not { } entry) return;
            var tone = PullRequestTones.Strongest(revealed.Select(r => PullRequestTones.From(r.Read.Data!)));
            entry.Tone = tone == PullRequestTone.None ? null : tone;
            Publish();
            if (revealed.Count == 0) {
                entry.Summary = null;
                entry.Access  = null;
            } else if (revealed.All(r => r.Read.CanReveal(_time))) {
                var summary = new RailPullRequestSummary(revealed.Select(r => new RailPullRequestMenuItem(
                    PullRequestWire.Subject(r.Link), r.Read.Data!.Title ?? r.Link.Title, LifecycleLabel(r.Read.Data!))).ToArray());
                if (entry.Summary is not { } previous || !SameSummary(previous, summary)) entry.Summary = summary;
                var remaining = revealed.Min(r => r.Read.RemainingSeconds(_time));
                entry.Access = (_time.GetTimestamp(), remaining);
                var readFor    = entry.ReadAt is { } at ? _time.GetElapsedTime(at).TotalSeconds : 0;
                var renewAfter = readFor + Math.Max(1, remaining - 10);
                entry.RefreshAfter = TimeSpan.FromSeconds(Math.Clamp(renewAfter, 1, _refreshEvery.TotalSeconds));
            }
            PublishSummaries();
        }
    }

    static string LifecycleLabel(PullRequestOverviewDto overview) => overview.Lifecycle switch {
        "merged" => "Merged",
        "closed" => "Closed",
        "draft" => "Draft",
        "open" when overview.IsDraft == true => "Draft",
        "open" => "Open",
        _ => "Unknown",
    };

    void Clear(string session, long version) {
        lock (_lock) {
            if (LiveEntry(session, version) is not { } entry) return;
            entry.Tone = null;
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
