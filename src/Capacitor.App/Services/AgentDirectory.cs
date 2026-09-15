using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IAgentDirectory {
    IObservableCache<AgentRow, string> Rows { get; }
    /// True while the server lane is not Connected — rail rows grey out on it.
    IObservable<bool> RemoteStale { get; }
    /// True while the local daemon reports the app's own server; replays on subscribe. A session
    /// id is unique only within one server, so nothing local may be matched to a server-lane
    /// session while this is false: the same id there names a different session.
    IObservable<bool> LocalDaemonOnAppServer { get; }
    /// Session id -> logical agent id, over the current rows. Replay-1, distinct by content.
    IObservable<IReadOnlyDictionary<string, string>> SessionAgents { get; }
    /// The vendor of the row whose SessionId matches, or null.
    string? VendorOfSession(string sessionId);
    /// Whether the local daemon has proven it hosts this agent — the daemon proved to be its
    /// server twin registers it. A shared agent id proves nothing: the dedup fails open.
    bool IsProvenLocalTwin(string agentId);
    /// A row for a launch the server accepted, standing until any lane reports the id. It is
    /// dropped by the caller on a launch failure, and expires on its own after ten minutes.
    void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model);
    void RemovePlaceholder(string agentId);
}

/// Merges the local daemon's agents with the server registry's into source-scoped rows.
/// Suppression is evidence-based, symmetric and pairwise — a row hides only where the twin daemon's
/// other lane holds a row for the SAME agent. While the twin is proven, its remote row hides
/// whenever the local socket is Connected (the local view is current), and the local row hides once
/// it isn't (the twin's own server row wins). Another daemon's rows never take part: an unproven
/// identity, an unpaired agent and a same-id agent elsewhere all keep both lanes' rows.
public sealed class AgentDirectory : IAgentDirectory, IDisposable {
    readonly SourceCache<AgentRow, string> _rows = new(r => r.Key);
    readonly CompositeDisposable _subscriptions = new();
    readonly IDaemonClientService _local;
    readonly RepoIdentityResolver _repoIdentity;
    readonly Func<string, string> _resolveLocalRepoRoot;
    readonly string? _localMachineId;
    readonly string? _appServerUrl;
    readonly object _lock = new();
    readonly BehaviorSubject<bool> _onAppServer = new(false);

    IReadOnlyList<DaemonInfo> _daemons = [];
    bool _localConnected;
    string? _localServerUrl;
    List<AgentInstanceDto> _remoteAgents = [];
    List<AgentStatusDto> _localAgents = [];
    List<PendingLaunchDto> _pendingLaunches = [];
    readonly Dictionary<string, AgentRow> _placeholders = new(StringComparer.Ordinal);
    static readonly TimeSpan PlaceholderTtl = TimeSpan.FromMinutes(10);
    FrozenSet<string> _twinAgents = FrozenSet<string>.Empty;

    public AgentDirectory(
            IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
            string? localMachineId, string? appServerUrl) {
        _local = local;
        _repoIdentity = repoIdentity;
        _resolveLocalRepoRoot = resolveLocalRepoRoot;
        _localMachineId = localMachineId;
        _appServerUrl = appServerUrl;

        RemoteStale = lane.Status.Select(s => s.State != ServerLaneState.Connected).DistinctUntilChanged();

        // ToCollection (not the raw changeset), matching remote.Agents below: whether a local row
        // shows depends on the whole remote set, so Recompute needs the full current local set on
        // every change — an incremental Add/Update/Remove-per-key handler can't express that.
        local.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _localAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);

        local.Pending.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _pendingLaunches = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);

        remote.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _remoteAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);
        remote.Daemons
            .Subscribe(d => { lock (_lock) _daemons = d; Recompute(); })
            .DisposeWith(_subscriptions);
        local.Status
            .Select(s => s.State == AttachState.Connected).DistinctUntilChanged()
            .Subscribe(c => { lock (_lock) _localConnected = c; Recompute(); })
            .DisposeWith(_subscriptions);
        local.Snapshots
            .Select(s => s.Daemon.ServerUrl).DistinctUntilChanged()
            .Subscribe(u => { lock (_lock) _localServerUrl = u; Recompute(); })
            .DisposeWith(_subscriptions);
    }

    public IObservableCache<AgentRow, string> Rows => _rows.AsObservableCache();
    public IObservable<bool> RemoteStale { get; }
    public IObservable<bool> LocalDaemonOnAppServer => _onAppServer;

    // The scope flag is an input, not just a consumer's filter: a flip changes the answer for rows
    // that never moved, so the map has to be republished on it.
    public IObservable<IReadOnlyDictionary<string, string>> SessionAgents => _rows.Connect()
        .QueryWhenChanged(q => (IReadOnlyList<AgentRow>)[.. q.Items])
        .StartWith((IReadOnlyList<AgentRow>)[.. _rows.Items])
        .CombineLatest(_onAppServer, SessionMap)
        .DistinctUntilChanged(new DictionaryEquality());

    public string? VendorOfSession(string sessionId) =>
        ServerSessionRows(_rows.Items, _onAppServer.Value)
            .Where(r => r.SessionId == sessionId).OrderBy(r => r.Origin).Select(r => r.Vendor).FirstOrDefault();

    public bool IsProvenLocalTwin(string agentId) => _twinAgents.Contains(agentId);

    public void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model) {
        lock (_lock) _placeholders[agentId] = AgentRow.Placeholder(agentId, vendor, repoPath, title, model, DateTime.UtcNow, RepoFor(repoPath));
        Recompute();
    }

    public void RemovePlaceholder(string agentId) {
        lock (_lock) _placeholders.Remove(agentId);
        Recompute();
    }

    /// Both session lookups answer for a server-lane session id. While the local daemon reports
    /// another server, its rows carry that server's ids and a match here is coincidence: the id
    /// names a different session, whose agent is not the local one.
    static IEnumerable<AgentRow> ServerSessionRows(IEnumerable<AgentRow> rows, bool localOnAppServer) =>
        localOnAppServer ? rows : rows.Where(r => r.Origin != AgentOrigin.Local);

    // Local sorts before Remote in AgentOrigin, so the ordered pass's TryAdd lets a local row win
    // a session claimed by both an unproven twin pair — proven suppression already keeps a twin's
    // remote row out of _rows entirely, so this tie only ever arises while the pairing is unproven.
    static IReadOnlyDictionary<string, string> SessionMap(IReadOnlyList<AgentRow> rows, bool localOnAppServer) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ServerSessionRows(rows, localOnAppServer).OrderBy(r => r.Origin))
            if (row.SessionId is { Length: > 0 } sid) map.TryAdd(sid, row.Id);
        return map.Count == 0 ? FrozenDictionary<string, string>.Empty : map;
    }

    sealed class DictionaryEquality : IEqualityComparer<IReadOnlyDictionary<string, string>> {
        public bool Equals(IReadOnlyDictionary<string, string>? x, IReadOnlyDictionary<string, string>? y) =>
            x is not null && y is not null && x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
        public int GetHashCode(IReadOnlyDictionary<string, string> obj) => obj.Count;
    }

    RepoIdentity RepoFor(string? repoPath) => repoPath is { Length: > 0 } path
        ? _repoIdentity.ForLocalRoot(PlatformPaths.Normalize(_resolveLocalRepoRoot(path)))
        : new RepoIdentity("path:", "No repository");

    AgentRow ProjectLocal(AgentStatusDto dto) => AgentRow.FromLocal(dto, RepoFor(dto.RepoPath));

    // The compute-then-edit pair must be one atomic unit under _lock: two triggers (e.g. a
    // socket-thread Status flip racing a SignalR-thread Daemons refresh) that read-then-edit as
    // separate critical sections can land their _rows.Edit calls out of read order, letting a
    // stale edit overwrite a fresher one. DynamicData's own cache lock is separate, so nesting
    // _rows.Edit inside _lock is deadlock-free. Owns BOTH lanes' rows in one pass, because
    // precedence is pairwise: while the twin is proven and the local socket is down, a local row
    // yields to the twin's own row for the SAME agent — but only to that row. Absent server data is
    // not evidence an agent ended (a private agent is never registered, and the registry has a
    // seed gap after every connect), so an unpaired local row stands as display-only history.
    void Recompute() {
        lock (_lock) {
            var twin = LocalDaemonTwin.Find(_daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            var twinProven = twin is not null;
            bool OnTwin(AgentInstanceDto a) =>
                twinProven && a.OwnerUserId == twin!.Value.OwnerUserId && a.DaemonName == twin.Value.DaemonName;

            // What says a retiring remote row means the local daemon took that agent over. It is a
            // takeover verdict, so it takes current local authority and not the pairing alone: the
            // socket up and the agent still live on it. A server verdict that ends the session
            // retires the same row, and read as a takeover it would hide the end.
            var liveLocally = _localAgents
                .Where(a => !ViewModels.SessionStatusDots.IsTerminal(a.Status))
                .Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            _twinAgents = twinProven && _localConnected
                ? _remoteAgents.Where(OnTwin).Select(a => a.AgentId).Where(liveLocally.Contains).ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;

            var remote = _remoteAgents
                .Where(a => a.Status is "Starting" or "Running")
                .Where(a => !(_localConnected && OnTwin(a)))
                .ToList();
            // The counterpart set is the TWIN's own rows, never every remote row: proving this
            // daemon's registry twin establishes no correspondence with an agent of the same id
            // running on some other daemon, which is a different agent entirely.
            var twinIds = remote.Where(OnTwin).Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
            var localRows = _localAgents
                .Where(a => !(twinProven && !_localConnected && twinIds.Contains(a.Id)))
                .Select(ProjectLocal);
            var next = localRows.Concat(remote.Select(AgentRow.FromRemote)).ToList();

            // A published row on either lane retires the launch's stand-ins for good; the daemon's
            // own pending entry only hides the app's placeholder, which returns should the entry
            // vanish without a row, until the failure notice or the TTL removes it.
            var published = next.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _placeholders.Keys.Where(published.Contains).ToList()) _placeholders.Remove(id);
            var cutoff = DateTime.UtcNow - PlaceholderTtl;
            foreach (var id in _placeholders.Where(kv => kv.Value.CreatedAt < cutoff).Select(kv => kv.Key).ToList()) _placeholders.Remove(id);
            var pendingRows = _pendingLaunches
                .Where(p => !published.Contains(p.Id))
                .Select(p => AgentRow.FromPending(p, RepoFor(p.RepoPath)))
                .ToList();
            var starting = pendingRows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            next.AddRange(pendingRows);
            next.AddRange(_placeholders.Values.Where(r => !starting.Contains(r.Id)));

            _rows.Edit(cache => {
                foreach (var key in cache.Keys.Where(k => !next.Any(r => r.Key == k)).ToList())
                    cache.RemoveKey(key);
                foreach (var row in next)
                    if (cache.Lookup(row.Key) is not { HasValue: true, Value: var existing } || existing != row)
                        cache.AddOrUpdate(row);
            });

            // Published inside the lock, like the row edit above, so no subscriber sees this
            // verdict and the rows disagreeing about which recompute produced them. A side that
            // does not canonicalize is not a match: no assertion can be made, and matching a local
            // session to a server one on that basis is what this exists to prevent.
            var onAppServer = ServerIdentity.SameServer(_localServerUrl, _appServerUrl);
            if (_onAppServer.Value != onAppServer) _onAppServer.OnNext(onAppServer);
        }
    }

    public void Dispose() {
        _subscriptions.Dispose();
        _rows.Dispose();
    }
}
