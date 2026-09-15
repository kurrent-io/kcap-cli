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
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(15);

    readonly IPullRequestSource _source;
    readonly TimeProvider _time;
    readonly TimeSpan _refreshEvery;
    readonly object _lock = new();
    readonly Dictionary<string, PullRequestTone> _tones = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _readAt = new(StringComparer.Ordinal);
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

    void Tick() {
        List<string> due;
        CancellationToken ct;
        lock (_lock) {
            if (_disposed) return;
            // Taken under the lock: a dispose racing the reads below would otherwise hand them a disposed source.
            ct = _cancel.Token;
            foreach (var session in _readAt.Keys.Where(s => !_sessions.Contains(s)).ToList()) _readAt.Remove(session);
            var gone = _tones.Keys.Where(s => !_sessions.Contains(s)).ToList();
            foreach (var session in gone) _tones.Remove(session);
            if (gone.Count > 0) Publish();
            due = _sessions
                .Where(s => !_inFlight.Contains(s) && (!_readAt.TryGetValue(s, out var at) || _time.GetElapsedTime(at) >= _refreshEvery))
                .ToList();
            foreach (var session in due) { _inFlight.Add(session); _readAt[session] = _time.GetTimestamp(); }
        }
        foreach (var session in due) _ = ReadAsync(session, ct);
    }

    async Task ReadAsync(string session, CancellationToken ct) {
        try {
            var capability = await _source.DiscoverAsync(false, ct).ConfigureAwait(false);
            if (capability.Kind != PullRequestCapabilityKind.Supported) {
                // Signed out, or a server without overviews, is a verdict; an unreachable one is not.
                if (capability.Kind != PullRequestCapabilityKind.Unavailable) Set(session, PullRequestTone.None);
                return;
            }
            var links = await _source.ListAsync(session, ct).ConfigureAwait(false);
            if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                if (links.Kind is PullRequestReadKind.SubjectUnavailable or PullRequestReadKind.SignedOut || links.AccessFailure is "invalid" or "denied") Set(session, PullRequestTone.None);
                return;
            }
            var tones = new List<PullRequestTone>();
            var denied = false;
            var missed = false;
            foreach (var link in links.Data.Items) {
                var read = await _source.OverviewAsync(session, PullRequestWire.Subject(link), ct).ConfigureAwait(false);
                // The same gate the reader applies: a read past its access window reveals nothing.
                if (read.CanReveal(_time)) tones.Add(PullRequestTones.From(read.Data!));
                else if (read.AccessFailure is "denied" or "invalid") denied = true;
                else missed = true;
            }
            // A denial clears the tone, as the card does, whatever the session's other PRs read. A
            // miss on any PR keeps the last tone whole: the readable remainder alone could only
            // understate it.
            if (denied) Set(session, PullRequestTone.None);
            else if (!missed) Set(session, PullRequestTones.Strongest(tones));
        } catch (OperationCanceledException) {
        } finally {
            lock (_lock) _inFlight.Remove(session);
        }
    }

    void Set(string session, PullRequestTone tone) {
        lock (_lock) {
            if (_disposed || !_sessions.Contains(session)) return;
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
        _cancel.Dispose();
    }
}
