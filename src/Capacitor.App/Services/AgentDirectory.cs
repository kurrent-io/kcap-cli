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
    /// Whether a daemon lane currently vouches for the row: a live status under a connected local
    /// socket on the app's server, or under a current registry. A retained row is history on
    /// display, and a server row never qualifies.
    bool IsHostedNow(AgentRow row);
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
    /// The row a rail click on this server-lane session id opens: a daemon row that survived the
    /// merge (local first), else its server row, else null.
    (string Id, AgentOrigin Origin)? RowForSession(string sessionId);
    /// Whether the local daemon has proven it hosts this agent — the daemon proved to be its
    /// server twin registers it. A shared agent id proves nothing: the dedup fails open.
    bool IsProvenLocalTwin(string agentId);
    /// The app saw this session's hosted agent end, which the server's listing can trail: its row
    /// reads ended until a daemon row hosts the session again.
    void NoteFinished(string sessionId) { }
    /// A row for a launch the server accepted, standing until the local lane publishes the id;
    /// a same-id row on the remote lane is a different agent and leaves it in place. It is
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
    readonly TimeProvider _time;
    // Nothing else need ever change in an idle directory, so expiry is this timer's job alone.
    readonly ITimer _placeholderExpiry;
    /// How long a server row keeps looking hosted after the daemon row for its session went away.
    public static readonly TimeSpan HandoverGrace = TimeSpan.FromSeconds(30);
    readonly ITimer _graceExpiry;
    readonly IServerSessionsService? _sessions;
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
    bool _registryCurrent;
    List<RepoSessionDto> _serverLive = [];
    List<RepoSessionDto> _serverMineLive = [];
    List<RepoSessionDto> _serverEnded = [];
    RepoSessionDto? _serverPinned;
    string? _serverPinnedId;
    RailScope _scope = RailScope.Mine;
    IReadOnlySet<string> _viewerIds = FrozenSet<string>.Empty;
    /// Session id → when its hosted presentation lapses; written while a daemon row suppresses the
    /// server row, read once none does.
    readonly Dictionary<string, DateTime> _hostedUntil = new(StringComparer.Ordinal);
    /// Listed session id → the checkout facts its daemon row last reported, for the server row
    /// that stands for the session once the daemon row is gone.
    readonly Dictionary<string, (string? RepoPath, string? WorktreePath, string? WorkLocation, string? BorrowedFrom, string? Branch)> _hostedFacts =
        new(StringComparer.Ordinal);
    /// Sessions whose hosted agent is known to have finished while the listing may still say live:
    /// a local row seen Completed, or a stop the app saw accepted. A live daemon row for the session
    /// clears the mark, and so does the listing once it says ended.
    readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    bool _disposed;

    public AgentDirectory(
            IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
            string? localMachineId, string? appServerUrl, TimeProvider time,
            IServerSessionsService? sessions = null) {
        _local = local;
        _repoIdentity = repoIdentity;
        _resolveLocalRepoRoot = resolveLocalRepoRoot;
        _localMachineId = localMachineId;
        _appServerUrl = appServerUrl;
        _time = time;
        _sessions = sessions;
        _placeholderExpiry = _time.CreateTimer(_ => Recompute(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _graceExpiry = _time.CreateTimer(_ => Recompute(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        remote.Current
            .Subscribe(current => { lock (_lock) _registryCurrent = current; Recompute(); })
            .DisposeWith(_subscriptions);
        if (sessions is not null) {
            // One subscription for all three sets: a recompute between two of them would judge a
            // live row against an ownership set from the previous read.
            sessions.Snapshots
                .Subscribe(snapshot => {
                    lock (_lock) {
                        _serverLive = [.. snapshot.Live];
                        _serverMineLive = [.. snapshot.MineLive];
                        _serverEnded = [.. snapshot.Ended];
                        _serverPinned = snapshot.Pinned;
                        _serverPinnedId = snapshot.PinnedSessionId;
                    }
                    Recompute();
                })
                .DisposeWith(_subscriptions);
            sessions.ScopeChanges
                .Subscribe(scope => { lock (_lock) _scope = scope; Recompute(); })
                .DisposeWith(_subscriptions);
            sessions.ViewerIds
                .Subscribe(ids => { lock (_lock) _viewerIds = ids; Recompute(); })
                .DisposeWith(_subscriptions);
        }

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
        // Subscribed before Status so a replayed snapshot's connected flag is overridden when
        // Status is currently Unreachable; on a live Connected event, Snapshots arrives ahead of
        // Status (DaemonClientService.Apply) and marks _localConnected before Recompute runs, so
        // remote twin rows do not flash while _localServerUrl is set and _localConnected is false.
        local.Snapshots
            .Subscribe(s => {
                lock (_lock) {
                    if (_localServerUrl == s.Daemon.ServerUrl && _localConnected) return;
                    _localServerUrl = s.Daemon.ServerUrl;
                    _localConnected = true;
                }
                Recompute();
            })
            .DisposeWith(_subscriptions);
        local.Status
            .Select(s => s.State == AttachState.Connected)
            .Subscribe(c => {
                lock (_lock) {
                    if (_localConnected == c) return;
                    _localConnected = c;
                }
                Recompute();
            })
            .DisposeWith(_subscriptions);
    }

    public IObservableCache<AgentRow, string> Rows => _rows.AsObservableCache();
    public IObservable<bool> RemoteStale { get; }
    public IObservable<bool> LocalDaemonOnAppServer => _onAppServer;

    public bool IsHostedNow(AgentRow row) {
        if (ViewModels.SessionStatusDots.IsTerminal(row.Status)) return false;
        lock (_lock) {
            return row.Origin switch {
                AgentOrigin.Local => _localConnected && _onAppServer.Value,
                AgentOrigin.Remote => _registryCurrent,
                _ => false,
            };
        }
    }

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

    public (string Id, AgentOrigin Origin)? RowForSession(string sessionId) {
        var rows = _rows.Items.ToList();
        var daemon = ServerSessionRows(rows, _onAppServer.Value)
            .Where(r => SessionIds.Canonical(r.SessionId) is { } own && (own == sessionId || SessionIds.ServerToken(own) == sessionId))
            .OrderBy(r => r.Origin)
            .FirstOrDefault();
        if (daemon is not null) return (daemon.Id, daemon.Origin);
        return rows.FirstOrDefault(r => r.Key == $"server:{sessionId}") is { } server ? (server.Id, server.Origin) : null;
    }

    public bool IsProvenLocalTwin(string agentId) => _twinAgents.Contains(agentId);

    public void NoteFinished(string sessionId) {
        lock (_lock) MarkFinished(sessionId);
        Recompute();
    }

    /// Under the lock. Both spellings the server may list the session under.
    void MarkFinished(string? sessionId) {
        if (SessionIds.Canonical(sessionId) is not { } own) return;
        _finished.Add(own);
        _finished.Add(SessionIds.ServerToken(own));
    }

    // Placeholders are keyed by the normalized id: the server accepts a launch under one spelling
    // and the daemon can publish it under another.
    public void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model) {
        if (AgentIds.Normalize(agentId) is not { } key) return;
        lock (_lock) _placeholders[key] = AgentRow.Placeholder(key, vendor, repoPath, title, model, _time.GetUtcNow().UtcDateTime, RepoFor(repoPath));
        Recompute();
    }

    public void RemovePlaceholder(string agentId) {
        if (AgentIds.Normalize(agentId) is not { } key) return;
        lock (_lock) _placeholders.Remove(key);
        Recompute();
    }

    /// Both session lookups answer for a server-lane session id. While the local daemon reports
    /// another server, its rows carry that server's ids and a match here is coincidence: the id
    /// names a different session, whose agent is not the local one.
    /// A server row is left out: nothing here can answer for a session no daemon hosts.
    static IEnumerable<AgentRow> ServerSessionRows(IEnumerable<AgentRow> rows, bool localOnAppServer) =>
        rows.Where(r => r.Origin != AgentOrigin.Server && (localOnAppServer || r.Origin != AgentOrigin.Local));

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

    static readonly RepoIdentity NoRepositoryIdentity = new("path:", "No repository");
    readonly Dictionary<string, RepoIdentity> _repoByPath = new(StringComparer.Ordinal);

    RepoIdentity RepoFor(string? repoPath) {
        if (repoPath is not { Length: > 0 } path) return NoRepositoryIdentity;
        if (_repoByPath.TryGetValue(path, out var cached)) return cached;
        var resolved = _repoIdentity.ForLocalRoot(PlatformPaths.Normalize(_resolveLocalRepoRoot(path)));
        _repoByPath[path] = resolved;
        return resolved;
    }

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
            // The expiry timer's callback can land after Dispose ran; the lock serialises the two
            // and the flag turns the late callback into a no-op instead of an edit of a disposed cache.
            if (_disposed) return;
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
                // The server registry uses lowercase `idle` for a live agent waiting between
                // turns; local daemon snapshots call that same live state `Running`.
                .Where(a => a.Status.Equals("Starting", StringComparison.OrdinalIgnoreCase)
                    || a.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    || a.Status.Equals("Idle", StringComparison.OrdinalIgnoreCase))
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
            if (_sessions is not null) MergeServerLane(next);

            // A published local row retires the launch's stand-ins for good; the daemon's own
            // pending entry only hides the app's placeholder, which returns should the entry vanish
            // without a row, until the failure notice or the TTL removes it. Only the local lane
            // counts: a same-id row on the remote lane is a different agent, and ids compare in
            // their normalized form because the two lanes spell a Guid differently.
            var published = next.Where(r => r.Origin == AgentOrigin.Local).Select(r => AgentIds.Normalize(r.Id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var id in _placeholders.Keys.Where(published.Contains).ToList()) _placeholders.Remove(id);
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var id in _placeholders.Where(kv => kv.Value.CreatedAt + PlaceholderTtl <= now).Select(kv => kv.Key).ToList()) _placeholders.Remove(id);
            // Infinite when nothing is pending; otherwise positive, since every expired entry was just removed.
            var nextExpiry = _placeholders.Count == 0 ? Timeout.InfiniteTimeSpan : _placeholders.Values.Min(r => r.CreatedAt) + PlaceholderTtl - now;
            _placeholderExpiry.Change(nextExpiry, Timeout.InfiniteTimeSpan);
            var pendingRows = _pendingLaunches
                .Where(p => AgentIds.Normalize(p.Id) is { } id && !published.Contains(id))
                .Select(p => AgentRow.FromPending(p, RepoFor(p.RepoPath)))
                .ToList();
            var starting = pendingRows.Select(r => AgentIds.Normalize(r.Id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            next.AddRange(pendingRows);
            next.AddRange(_placeholders.Values.Where(r => !starting.Contains(r.Id)));

            var nextKeys = next.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            _rows.Edit(cache => {
                foreach (var key in cache.Keys.Where(k => !nextKeys.Contains(k)).ToList())
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

    /// One session, one row, decided by evidence. A daemon row is live only while its lane is
    /// current: the local socket Connected, or a registry read that succeeded in this connection.
    /// A live daemon row hides the server row for its session and stamps the hand-over grace; a
    /// finished local row yields to the server's, which reads ended from then on even while the
    /// listing still says live; a Failed local row stays, since its reason is what the user needs; a
    /// cached row the lane merely retains yields to a server row that says the session ended and
    /// hides one that says it is still active. Under Mine, the scoped live
    /// listing is the ownership proof for every daemon row the server has listed, and a registry row
    /// the server has not listed is hidden while a local one is presumed the viewer's own. The pinned
    /// session is exempt from that filter, and joins the merge under either scope.
    void MergeServerLane(List<AgentRow> next) {
        var now = _time.GetUtcNow().UtcDateTime;
        var localMatchable = ServerIdentity.SameServer(_localServerUrl, _appServerUrl);
        var registryMatchable = _appServerUrl is not null;
        // The ended prefix counts as listed too: under Mine it is owner-filtered, so a session in it
        // is as much the viewer's as one in the scoped live listing.
        var listed = new Dictionary<string, RepoSessionDto>(StringComparer.Ordinal);
        foreach (var dto in _serverLive) listed[dto.SessionId] = dto;
        foreach (var dto in _serverMineLive) listed.TryAdd(dto.SessionId, dto);
        foreach (var dto in _serverEnded) listed.TryAdd(dto.SessionId, dto);
        if (_serverPinned is { } pinned) listed.TryAdd(pinned.SessionId, pinned);
        var mine = _serverMineLive.Select(dto => dto.SessionId).ToHashSet(StringComparer.Ordinal);
        if (_scope == RailScope.Mine) foreach (var dto in _serverEnded) mine.Add(dto.SessionId);

        // A pinned session no Mine listing proves the viewer's is judged by owner id, never by scope.
        bool? Ownership(RepoSessionDto dto, string sid) =>
            sid == _serverPinnedId && !mine.Contains(sid) ? ViewerOwns(dto) : OwnerIsViewer(dto, mine.Contains(sid));
        bool Matchable(AgentRow row) => row.Origin == AgentOrigin.Local ? localMatchable : registryMatchable;
        // The daemon spells a hook's GUID as it came and an ACP id as the vendor gave it; the server
        // files the first as 32 hex digits and the second under its own token. The server's spelling
        // is the key throughout, so each daemon row is resolved to it once.
        string? ListedId(AgentRow row) {
            if (SessionIds.Canonical(row.SessionId) is not { } own) return null;
            if (listed.ContainsKey(own)) return own;
            var token = SessionIds.ServerToken(own);
            return listed.ContainsKey(token) ? token : null;
        }
        var listedIdOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in next) if (row.Origin is AgentOrigin.Local or AgentOrigin.Remote) listedIdOf[row.Key] = ListedId(row);
        if (localMatchable)
            foreach (var row in next)
                if (row is { Origin: AgentOrigin.Local, Status: "Completed" }) MarkFinished(row.SessionId);

        for (var i = 0; i < next.Count; i++) {
            var row = next[i];
            if (!Matchable(row) || listedIdOf.GetValueOrDefault(row.Key) is not { } sid || !listed.TryGetValue(sid, out var dto)) continue;
            // A daemon row carries the listing's branch, never its own; the local daemon's is on its dto.
            var daemonBranch = row.Origin == AgentOrigin.Local ? _localAgents.FirstOrDefault(a => a.Id == row.Id)?.Branch : null;
            if (row.RepoPath is not null || row.WorktreePath is not null)
                _hostedFacts[sid] = (row.RepoPath, row.WorktreePath, row.WorkLocation, row.BorrowedFrom,
                    string.IsNullOrWhiteSpace(daemonBranch) ? row.Branch ?? dto.Branch : daemonBranch);
            next[i] = row with {
                OwnerUserId = dto.Owner?.UserId,
                OwnerName = dto.Owner?.DisplayName ?? dto.Owner?.Username,
                OwnerAvatarUrl = dto.Owner?.AvatarUrl,
                OwnerIsViewer = Ownership(dto, sid),
                LastActivityAt = dto.LastActivityAt.UtcDateTime,
                ListedPullRequests = dto.PullRequests,
                Branch = row.Branch ?? dto.Branch,
            };
        }
        if (_scope == RailScope.Mine) {
            next.RemoveAll(row => {
                if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Remote) || !Matchable(row)) return false;
                if (listedIdOf.GetValueOrDefault(row.Key) is { } sid) return !mine.Contains(sid) && sid != _serverPinnedId;
                // The registry names the owner before the listing names the session.
                return row.Origin == AgentOrigin.Remote && !(row.OwnerUserId is { } owner && _viewerIds.Contains(owner));
            });
        }

        var source = (_scope == RailScope.Mine ? _serverMineLive : _serverLive).Concat(_serverEnded);
        var byId = new Dictionary<string, RepoSessionDto>(StringComparer.Ordinal);
        foreach (var dto in source) byId[dto.SessionId] = dto;
        if (_serverPinnedId is { } pinnedId && listed.TryGetValue(pinnedId, out var pinnedDto)) byId.TryAdd(pinnedId, pinnedDto);
        foreach (var dto in byId.Values) {
            var sid = dto.SessionId;
            var serverEnded = dto.Status != "active";
            var suppressServer = false;
            var hostedNow = false;
            next.RemoveAll(row => {
                if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Remote) || listedIdOf.GetValueOrDefault(row.Key) != sid || !Matchable(row)) return false;
                if (row.Origin == AgentOrigin.Remote) {
                    // A stale registry is not an ended session: the retained row stands while the
                    // listing says live, and yields only to the server's word that it ended.
                    if (!_registryCurrent) {
                        if (serverEnded) return true;
                        suppressServer = true;
                        return false;
                    }
                    suppressServer = true;
                    hostedNow = true;
                    return false;
                }
                if (!_localConnected) {
                    if (serverEnded) return true;
                    suppressServer = true;
                    return false;
                }
                switch (row.Status) {
                    case "Completed": return true;
                    case "Failed": suppressServer = true; return false;
                    default: suppressServer = true; hostedNow = true; return false;
                }
            });
            if (hostedNow) {
                _hostedUntil[sid] = now + HandoverGrace;
                _finished.Remove(sid);
            }
            if (serverEnded) _finished.Remove(sid);
            if (suppressServer) continue;
            var hosted = _hostedUntil.TryGetValue(sid, out var until) && until > now;
            // The listing says live until the server folds the end; the agent's own end is the newer word.
            var shown = !serverEnded && _finished.Contains(sid) ? dto with { Status = "ended", EndedAt = dto.LastActivityAt } : dto;
            var server = AgentRow.FromServer(shown, hosted) with { OwnerIsViewer = Ownership(dto, sid) };
            // The listing carries no checkout and often no cwd; what the daemon row reported while it
            // hosted the session is the evidence, kept only where the viewer still has full access.
            if (dto.AccessLevel == "full" && _hostedFacts.TryGetValue(sid, out var facts))
                server = server with {
                    RepoPath = facts.RepoPath ?? server.RepoPath, WorktreePath = facts.WorktreePath,
                    WorkLocation = facts.WorkLocation, BorrowedFrom = facts.BorrowedFrom, Branch = facts.Branch ?? server.Branch,
                };
            next.Add(server);
        }

        foreach (var sid in _hostedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _hostedUntil.Remove(sid);
        var nextLapse = _hostedUntil.Count == 0 ? Timeout.InfiniteTimeSpan : _hostedUntil.Values.Min() - now;
        _graceExpiry.Change(nextLapse, Timeout.InfiniteTimeSpan);
    }

    /// Under Mine the scoped listing answers; under Team only the account's ids can, and only once known.
    bool? OwnerIsViewer(RepoSessionDto dto, bool inMine) => _scope == RailScope.Mine ? inMine : ViewerOwns(dto);

    bool? ViewerOwns(RepoSessionDto dto) =>
        _viewerIds.Count > 0 && dto.Owner is { } owner ? _viewerIds.Contains(owner.UserId) : null;

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _placeholderExpiry.Dispose();
            _graceExpiry.Dispose();
        }
        _subscriptions.Dispose();
        _rows.Dispose();
    }
}
