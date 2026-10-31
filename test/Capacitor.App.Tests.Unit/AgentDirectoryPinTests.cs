using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class AgentDirectoryPinTests {
    const string Server = "http://localhost:9999"; // FakeDaemonClientService.Snap's default ServerUrl

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerSessionsService Sessions, AgentDirectory Dir) Build() {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var sessions = new FakeServerSessionsService();
        var dir = new AgentDirectory(
            local, remote, new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p,
            "m1", Server, new FakeTimeProvider(), sessions);
        return (local, remote, sessions, dir);
    }

    static RepoSessionDto Listed(string id, string status = "ended", string owner = "me", string ownerName = "Alexey", string access = "full") => new() {
        SessionId = id, Status = status, AccessLevel = access, Vendor = "claude", Title = id,
        Owner = new() { UserId = owner, DisplayName = ownerName, AvatarUrl = $"https://avatars.example/{owner}" },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch : null,
    };

    static AgentStatusDto LocalAgent(string id, string? sessionId = null, string status = "Running") => new(
        Id: id, Kind: "agent", Vendor: "claude", RepoPath: "/r", Status: status,
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, SessionId: sessionId);

    static AgentInstanceDto Remote(string id, string daemon, string owner, string sessionId) => new() {
        AgentId = id, Status = "Running", DaemonName = daemon, OwnerUserId = owner, Vendor = "claude",
        RepoOwner = "o", RepoName = "r", SessionId = sessionId,
    };

    static void LocalConnected(FakeDaemonClientService local, string serverUrl = Server) {
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: serverUrl));
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
    }

    static void MineLive(FakeServerSessionsService sessions, string id) {
        var row = Listed(id, "active");
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);
    }

    static void Pin(FakeServerSessionsService sessions, RepoSessionDto? row, string id) {
        sessions.PinnedSubject.OnNext(row);
        sessions.PinnedIdSubject.OnNext(id);
    }

    [Test]
    public async Task Pinned_teammate_session_shows_under_mine_with_the_teammates_owner() {
        var (_, _, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        sessions.ViewerIdsSubject.OnNext(new HashSet<string>(StringComparer.Ordinal) { "me" });
        Pin(sessions, Listed("S", owner: "other", ownerName: "Tony Young", access: ""), "S");

        var row = dir.Rows.Lookup("server:S");
        await Assert.That(row.HasValue).IsTrue();
        await Assert.That(row.Value.OwnerIsViewer).IsFalse();
        await Assert.That(row.Value.OwnerName).IsEqualTo("Tony Young");
    }

    [Test]
    public async Task Pinned_session_hosted_on_a_teammates_daemon_keeps_its_daemon_row_under_mine() {
        var (_, remote, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        sessions.ViewerIdsSubject.OnNext(new HashSet<string>(StringComparer.Ordinal) { "me" });
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "other", sessionId: "S"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();

        Pin(sessions, Listed("S", "active", owner: "other", ownerName: "Tony Young", access: ""), "S");

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
        await Assert.That(dir.RowForSession("S")).IsEqualTo(("b1", AgentOrigin.Remote));
    }

    /// Both twins survive the merge while the pairing is unproven; the tie goes to the local row.
    [Test]
    public async Task Row_for_session_prefers_the_local_row_of_an_unproven_twin() {
        var (local, remote, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "me", sessionId: "S"));
        MineLive(sessions, "S");
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();

        await Assert.That(dir.RowForSession("S")).IsEqualTo(("a1", AgentOrigin.Local));
    }

    [Test]
    public async Task Row_for_session_returns_a_failed_local_row_over_the_server_row() {
        var (local, _, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Failed"));
        sessions.EndedCache.AddOrUpdate(Listed("S"));

        await Assert.That(dir.RowForSession("S")).IsEqualTo(("a1", AgentOrigin.Local));
    }

    [Test]
    public async Task Row_for_session_matches_a_dashed_daemon_session_id_to_the_server_spelling() {
        var (local, _, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "0D1E2F30-4455-6677-8899-AABBCCDDEEFF"));
        MineLive(sessions, "0d1e2f30445566778899aabbccddeeff");

        await Assert.That(dir.RowForSession("0d1e2f30445566778899aabbccddeeff")).IsEqualTo(("a1", AgentOrigin.Local));
    }

    /// On another server the same id names a different session, so only the server row answers.
    [Test]
    public async Task Row_for_session_ignores_local_rows_while_the_local_daemon_is_on_another_server() {
        var (local, _, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local, serverUrl: "https://elsewhere.example");
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        sessions.EndedCache.AddOrUpdate(Listed("S"));
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();

        await Assert.That(dir.RowForSession("S")).IsEqualTo(("S", AgentOrigin.Server));
    }

    [Test]
    public async Task Row_for_session_returns_the_server_row_for_a_session_only_session() {
        var (_, _, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        sessions.EndedCache.AddOrUpdate(Listed("S"));

        await Assert.That(dir.RowForSession("S")).IsEqualTo(("S", AgentOrigin.Server));
        await Assert.That(dir.RowForSession("unknown")).IsNull();
    }
    /// What the real service publishes for one pinned session under a scope: live rows in the
    /// unscoped listing, the viewer's own also in the scoped one, ended rows only where the scope
    /// lists them, and the pinned row only while no listing carries it.
    static void PublishPinned(FakeServerSessionsService sessions, RailScope scope, RepoSessionDto dto, bool viewerOwns) {
        sessions.LiveCache.Clear();
        sessions.MineLiveCache.Clear();
        sessions.EndedCache.Clear();
        var listed = false;
        if (dto.Status == "active") {
            sessions.LiveCache.AddOrUpdate(dto);
            if (scope == RailScope.Mine && viewerOwns) sessions.MineLiveCache.AddOrUpdate(dto);
            listed = true;
        } else if (scope == RailScope.Team || viewerOwns) {
            sessions.EndedCache.AddOrUpdate(dto);
            listed = true;
        }
        Pin(sessions, listed ? null : dto, dto.SessionId);
    }

    /// A search-opened session resolves to the same row under either scope, and a daemon row that
    /// survives the merge never has the server row standing beside it.
    [Test]
    [Arguments("hosted-local", "a1", AgentOrigin.Local)]
    [Arguments("hosted-remote", "b1", AgentOrigin.Remote)]
    [Arguments("failed-local", "a1", AgentOrigin.Local)]
    [Arguments("interrupted-local", "a1", AgentOrigin.Local)]
    [Arguments("unproven-twin", "a1", AgentOrigin.Local)]
    [Arguments("session-only", "S", AgentOrigin.Server)]
    [Arguments("teammate-live-only", "S", AgentOrigin.Server)]
    public async Task A_pinned_session_resolves_to_the_same_row_under_mine_and_team(string scenario, string expectedId, AgentOrigin expectedOrigin) {
        var (local, remote, sessions, dir) = Build();
        using var _d = dir;
        using var _s = sessions;
        using var _r = remote;
        sessions.ViewerIdsSubject.OnNext(new HashSet<string>(StringComparer.Ordinal) { "me" });
        LocalConnected(local);
        RepoSessionDto dto;
        var viewerOwns = true;
        switch (scenario) {
            case "hosted-local":
                local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
                dto = Listed("S", "active");
                break;
            case "hosted-remote":
                remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "other", sessionId: "S"));
                dto = Listed("S", "active", owner: "other", ownerName: "Tony Young", access: "");
                viewerOwns = false;
                break;
            case "failed-local":
                local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Failed"));
                dto = Listed("S");
                break;
            case "interrupted-local":
                local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Interrupted"));
                dto = Listed("S");
                break;
            case "unproven-twin":
                local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
                remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "me", sessionId: "S"));
                dto = Listed("S", "active");
                break;
            case "session-only":
                dto = Listed("S", owner: "other", ownerName: "Tony Young", access: "");
                viewerOwns = false;
                break;
            case "teammate-live-only":
                dto = Listed("S", "active", owner: "other", ownerName: "Tony Young", access: "");
                viewerOwns = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var resolved = new List<(string Id, AgentOrigin Origin)?>();
        var serverBesideDaemon = new List<bool>();
        foreach (var scope in new[] { RailScope.Team, RailScope.Mine }) {
            sessions.Scope = scope;
            PublishPinned(sessions, scope, dto, viewerOwns);
            resolved.Add(dir.RowForSession("S"));
            var rows = dir.Rows.Items.Where(r => r.SessionId == "S" || r.Key == "server:S").ToList();
            serverBesideDaemon.Add(rows.Any(r => r.Origin == AgentOrigin.Server) && rows.Any(r => r.Origin != AgentOrigin.Server));
        }

        await Assert.That(resolved).IsEquivalentTo(new (string, AgentOrigin)?[] { (expectedId, expectedOrigin), (expectedId, expectedOrigin) });
        await Assert.That(serverBesideDaemon).IsEquivalentTo([false, false]);
    }
}
