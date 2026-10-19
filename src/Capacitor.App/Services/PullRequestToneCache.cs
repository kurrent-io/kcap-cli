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

    readonly IPullRequestSource _source;
    readonly TimeProvider _time;
    readonly TimeSpan _refreshEvery;
    readonly object _lock = new();
    readonly Dictionary<string, PullRequestTone> _tones = new(StringComparer.Ordinal);
    readonly Dictionary<string, RailPullRequestSummary> _summaries = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long Started, double Seconds)> _summaryAccess = new(StringComparer.Ordinal);
    readonly BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>> _summaryPublished = new(FrozenDictionary<string, RailPullRequestSummary>.Empty);
    readonly Dictionary<string, TimeSpan> _refreshAfter = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _readAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    long _nextVersion;
    readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    readonly BehaviorSubject<IReadOnlyDictionary<string, PullRequestTone>> _published = new(FrozenDictionary<string, PullRequestTone>.Empty);
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
            _versions[session] = ++_nextVersion;
            _readAt.Remove(session);
            _refreshAfter.Remove(session);
        }
        Tick();
    }

    void Tick() {
        List<(string Session, long Version)> due;
        CancellationToken ct;
        lock (_lock) {
            if (_disposed) return;
            // Taken under the lock: a dispose racing the reads below would otherwise hand them a disposed source.
            ct = _cancel.Token;
            foreach (var session in _versions.Keys.Where(s => !_sessions.Contains(s)).ToList()) _versions.Remove(session);
            foreach (var session in _sessions)
                if (!_versions.ContainsKey(session)) _versions[session] = ++_nextVersion;
            foreach (var session in _readAt.Keys.Where(s => !_sessions.Contains(s)).ToList()) { _readAt.Remove(session); _refreshAfter.Remove(session); }
            var gone = _tones.Keys.Where(s => !_sessions.Contains(s)).ToList();
            foreach (var session in gone) _tones.Remove(session);
            if (gone.Count > 0) Publish();
            var expired = _summaries.Keys.Where(s => !_sessions.Contains(s)
                || _time.GetElapsedTime(_summaryAccess[s].Started).TotalSeconds > _summaryAccess[s].Seconds - 5).ToList();
            foreach (var session in expired) { _summaries.Remove(session); _summaryAccess.Remove(session); _refreshAfter.Remove(session); }
            if (expired.Count > 0) PublishSummaries();
            due = _sessions
                .Where(s => !_inFlight.Contains(s) && (!_readAt.TryGetValue(s, out var at) || _time.GetElapsedTime(at) >= _refreshAfter.GetValueOrDefault(s, _refreshEvery)))
                .Select(s => (Session: s, Version: _versions[s])).ToList();
            foreach (var (session, _) in due) { _inFlight.Add(session); _readAt[session] = _time.GetTimestamp(); }
        }
        foreach (var (session, version) in due) _ = ReadAsync(session, version, ct);
    }

    async Task ReadAsync(string session, long version, CancellationToken ct) {
        try {
            // Only a successfully renewed summary earns the faster access-window cadence.
            lock (_lock) {
                if (_disposed || _versions.GetValueOrDefault(session) != version) return;
                _refreshAfter.Remove(session);
            }
            var capability = await _source.DiscoverAsync(false, ct).ConfigureAwait(false);
            if (capability.Kind != PullRequestCapabilityKind.Supported) {
                // Signed out, or a server without overviews, is a verdict; an unreachable one is not.
                if (capability.Kind != PullRequestCapabilityKind.Unavailable) Clear(session, version);
                return;
            }
            var links = await _source.ListAsync(session, ct).ConfigureAwait(false);
            if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                if (links.Kind is PullRequestReadKind.SubjectUnavailable or PullRequestReadKind.SignedOut || links.AccessFailure is "invalid" or "denied" || links.Reason is "identity_changed" or "integration_changed" or "no_reader") Clear(session, version);
                return;
            }
            var tones = new List<PullRequestTone>();
            var reads = new List<PullRequestRead<PullRequestOverviewDto>>();
            var items = links.Data.Items.DistinctBy(PullRequestWire.Subject).ToArray();
            var denied = false;
            var missed = false;
            foreach (var link in items) {
                var read = await _source.OverviewAsync(session, PullRequestWire.Subject(link), ct).ConfigureAwait(false);
                // The same gate the reader applies: a read past its access window reveals nothing.
                if (read.CanReveal(_time)) {
                    tones.Add(PullRequestTones.From(read.Data!));
                    reads.Add(read);
                }
                else if (read.AccessFailure is "denied" or "invalid" || read.Kind is PullRequestReadKind.SignedOut or PullRequestReadKind.SubjectUnavailable
                    || read.Reason is "identity_changed" or "integration_changed" or "no_reader") denied = true;
                else missed = true;
            }
            // A denial clears the tone, as the card does, whatever the session's other PRs read. A
            // miss on any PR keeps the last tone whole: the readable remainder alone could only
            // understate it.
            if (denied) Clear(session, version);
            else if (!missed) {
                Set(session, PullRequestTones.Strongest(tones), version);
                lock (_lock) {
                    if (_disposed || _versions.GetValueOrDefault(session) != version) return;
                    if (items.Length == 0) { _summaries.Remove(session); _summaryAccess.Remove(session); }
                    else if (reads.All(read => read.CanReveal(_time))) {
                        var summary = new RailPullRequestSummary(items.Select((link, i) => new RailPullRequestMenuItem(PullRequestWire.Subject(link),
                                reads[i].Data!.Title ?? link.Title, LifecycleLabel(reads[i].Data!))).ToArray());
                        if (!_summaries.TryGetValue(session, out var previous) || !SameSummary(previous, summary))
                            _summaries[session] = summary;
                        var remaining = reads.Min(r => r.RemainingSeconds(_time));
                        _summaryAccess[session] = (_time.GetTimestamp(), remaining);
                        var renewAfter = _time.GetElapsedTime(_readAt[session]).TotalSeconds + Math.Max(1, remaining - 10);
                        _refreshAfter[session] = TimeSpan.FromSeconds(Math.Clamp(renewAfter, 1, _refreshEvery.TotalSeconds));
                    }
                    PublishSummaries();
                }
            }
        } catch (OperationCanceledException) {
        } finally {
            bool refresh;
            lock (_lock) {
                _inFlight.Remove(session);
                refresh = !_readAt.ContainsKey(session);
            }
            // Refresh may have invalidated this read while it was in flight.
            if (refresh) Tick();
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
            if (_disposed || _versions.GetValueOrDefault(session) != version) return;
            _tones.Remove(session);
            _summaries.Remove(session);
            _summaryAccess.Remove(session);
            _refreshAfter.Remove(session);
            Publish();
            PublishSummaries();
        }
    }

    void PublishSummaries() {
        if (_summaryPublished.Value.Count == _summaries.Count
            && _summaryPublished.Value.All(p => _summaries.GetValueOrDefault(p.Key) is { } next
                && SameSummary(next, p.Value))) return;
        _summaryPublished.OnNext(_summaries.Count == 0 ? FrozenDictionary<string, RailPullRequestSummary>.Empty
            : _summaries.ToFrozenDictionary(StringComparer.Ordinal));
    }

    static bool SameSummary(RailPullRequestSummary left, RailPullRequestSummary right) =>
        left.MenuItems.SequenceEqual(right.MenuItems);

    void Set(string session, PullRequestTone tone, long version) {
        lock (_lock) {
            if (_disposed || _versions.GetValueOrDefault(session) != version) return;
            if (tone == PullRequestTone.None) _tones.Remove(session);
            else _tones[session] = tone;
            Publish();
        }
    }

    void Publish() => _published.OnNext(_tones.Count == 0
        ? FrozenDictionary<string, PullRequestTone>.Empty
        : _tones.ToFrozenDictionary(StringComparer.Ordinal));

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
