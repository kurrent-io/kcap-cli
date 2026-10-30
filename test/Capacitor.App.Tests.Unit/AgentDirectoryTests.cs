using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class AgentDirectoryTests {
    const string Server = "http://localhost:9999"; // FakeDaemonClientService.Snap's default ServerUrl

    static AgentInstanceDto Remote(
            string id, string daemon = "work-mac", string owner = "u1", string status = "Running",
            string? sessionId = null) =>
        new() {
            AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = owner, Vendor = "claude",
            RepoOwner = "o", RepoName = "r", SessionId = sessionId,
        };

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerLane Lane, AgentDirectory Dir) Build(
            string? machineId = "m1") {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var lane = new FakeServerLane();
        var dir = new AgentDirectory(
            local, remote, lane, new RepoIdentityResolver(_ => null), p => p,
            machineId, Server, TimeProvider.System);
        return (local, remote, lane, dir);
    }

    static AgentStatusDto LocalAgent(string id, string? sessionId = null, string vendor = "claude", string status = "Running") => new(
        Id: id, Kind: "agent", Vendor: vendor, RepoPath: "/r", Status: status,
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null, SessionId: sessionId);

    [Test]
    public async Task LocalAndRemoteRowsMerge() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.Cache.AddOrUpdate(Remote("b1"));
        await Assert.That(dir.Rows.Count).IsEqualTo(2);
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    [Test]
    public async Task TwinAgentsSuppressWhileLocalConnected() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        // Local daemon "daemon-a" on machine m1, connected, reporting Server.
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        remote.Cache.AddOrUpdate(Remote("b2", daemon: "home-pc"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse(); // twin's agent suppressed
        await Assert.That(dir.Rows.Lookup("remote:b2").HasValue).IsTrue();  // other machine stands
    }

    [Test]
    public async Task SuppressionLiftsWhenLocalUnreachable() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    /// While the twin is proven, precedence flips WITH the local socket, one agent at a time:
    /// Connected shows the local row and suppresses the remote twin's; disconnected, the local row
    /// yields to the server's row for that SAME agent.
    [Test]
    public async Task ProvenTwinYieldsToTheServersRowOnceLocalDisconnects() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a"));

        // Twin proven, local Connected: the existing rule — local row shows, its remote twin
        // stays hidden.
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsFalse();

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        // Local socket lost: the server's own row for this agent is the current one and wins.
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsTrue();

        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/2"]));

        // Local reconnects: its retained row (never removed at the source, only suppressed while
        // disconnected) returns, and the remote twin's row is suppressed again.
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsFalse();
    }

    /// Missing server data is not evidence an agent ended: a private agent is never registered at
    /// all, and the registry has a seed gap after every connect. An unpaired local row therefore
    /// survives a proven twin's disconnect as display-only history, and a remote row that later
    /// disappears leaves the local one standing rather than erasing the session from view.
    [Test]
    public async Task ADisconnectedTwinKeepsLocalRowsTheServerHasNoRowFor() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));   // registered server-side
        local.Agents.AddOrUpdate(LocalAgent("p1"));   // private: never registered
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a"));

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsFalse(); // paired: the server's row wins
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("local:p1").HasValue).IsTrue();  // unpaired: stands

        remote.Cache.RemoveKey("a1"); // the server's own view: this agent has ended

        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
    }

    /// Proving the twin establishes correspondence with THAT daemon's rows and no others, so a
    /// same-id agent on a different daemon — a different agent that merely shares an id — can never
    /// stand in for the local one, however stale the local socket is.
    [Test]
    public async Task ADisconnectedTwinIgnoresASameIdAgentOnAnotherDaemon() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("x1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("x1", daemon: "daemon-b", owner: "u2"));

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        await Assert.That(dir.Rows.Lookup("local:x1").HasValue).IsTrue();  // no twin-side row for x1
        await Assert.That(dir.Rows.Lookup("remote:x1").HasValue).IsTrue();

        remote.Cache.AddOrUpdate(Remote("x1", daemon: "daemon-a")); // now the twin's OWN row for x1

        await Assert.That(dir.Rows.Lookup("local:x1").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("remote:x1").HasValue).IsTrue();
    }

    [Test]
    public async Task UncertainTwinFailsOpenToDuplicates() {
        var (local, remote, _, dir) = Build(machineId: null); // no persisted machine id
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a")); // same agent id, both lanes

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsTrue(); // two rows, never hidden
    }

    [Test]
    public async Task EndedRemoteAgentsAreNotRows() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1", status: "Completed"));
        await Assert.That(dir.Rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LowercaseIdleRemoteAgentsRemainVisibleAndUseTheCanonicalStatus() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1", status: "idle"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b1").Value.Status).IsEqualTo("Idle");
    }

    [Test]
    public async Task NoOpRecomputeSkipsUpdateChanges() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();

        var updateCount = 0;
        using var sub = dir.Rows.Connect().Subscribe(changes => {
            foreach (var change in changes)
                if (change.Reason == ChangeReason.Update) updateCount++;
        });

        remote.DaemonsSubject.OnNext([]); // recompute runs again; b1's row is unchanged

        await Assert.That(updateCount).IsEqualTo(0);
    }

    [Test]
    public async Task RemoteStaleTracksLane() {
        var (_, _, lane, dir) = Build();
        using var _d = dir;
        bool? stale = null;
        using var sub = dir.RemoteStale.Subscribe(s => stale = s);
        await Assert.That(stale).IsTrue();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Assert.That(stale).IsFalse();
    }

    /// The twin row (id "z9", same session) is unproven — no matching DaemonInfo/local-connected
    /// state — so both rows stand; SessionAgents must still resolve the tie to the LOCAL row's id.
    [Test]
    public async Task Session_agents_maps_every_row_with_a_session_and_prefers_the_local_row_on_a_tie() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap()); // the daemon is on the app's server
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "s1"));
        remote.Cache.AddOrUpdate(Remote("z9", daemon: "elsewhere", owner: "u2", sessionId: "s1"));
        remote.Cache.AddOrUpdate(Remote("r2", daemon: "elsewhere", owner: "u2", sessionId: "s2"));

        IReadOnlyDictionary<string, string>? map = null;
        using var sub = dir.SessionAgents.Subscribe(m => map = m);

        await Assert.That(map!["s1"]).IsEqualTo("a1");
        await Assert.That(map["s2"]).IsEqualTo("r2");
        await Assert.That(dir.VendorOfSession("s2")).IsNotNull();
    }

    /// A session id is unique only within one server. While the local daemon reports another one,
    /// its rows carry that server's ids, so a server-lane session sharing an id is the remote row's
    /// — resolving it to the local agent stamps that session's cards and its rail pip onto an
    /// unrelated process.
    [Test]
    public async Task Server_session_lookups_skip_local_rows_while_the_daemon_is_on_another_server() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: "http://elsewhere:8080"));
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "s1", vendor: "codex"));
        remote.Cache.AddOrUpdate(Remote("r1", daemon: "elsewhere", owner: "u2", sessionId: "s1"));

        IReadOnlyDictionary<string, string>? map = null;
        using var sub = dir.SessionAgents.Subscribe(m => map = m);

        await Assert.That(map!["s1"]).IsEqualTo("r1");
        await Assert.That(dir.VendorOfSession("s1")).IsEqualTo("claude");

        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());

        await Assert.That(map["s1"]).IsEqualTo("a1");
        await Assert.That(dir.VendorOfSession("s1")).IsEqualTo("codex");
    }

    /// Twin proof is a takeover verdict, so the pairing alone is not enough: with the local socket
    /// down, a remote row retiring is the server's own verdict on the agent and the local row that
    /// stands is history. A local row that has itself finished says the same.
    [Test]
    public async Task Twin_proof_needs_the_local_lane_connected_and_the_agent_live_on_it() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a"));

        await Assert.That(dir.IsProvenLocalTwin("a1")).IsTrue();

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(dir.IsProvenLocalTwin("a1")).IsFalse();

        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/2"]));
        await Assert.That(dir.IsProvenLocalTwin("a1")).IsTrue();

        local.Agents.AddOrUpdate(LocalAgent("a1", status: "Completed"));
        await Assert.That(dir.IsProvenLocalTwin("a1")).IsFalse();
    }

    /// DaemonClientService.Apply(Connected) publishes Snapshots, Agents, and Pending before Status
    /// flips to Connected. That sequence must never briefly emit the twin's remote row while
    /// Snapshots has set the server URL ahead of Status.
    [Test]
    public async Task LocalConnectSequenceDoesNotFlashRemoteTwinRowsBeforeStatusFlips() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a", status: "idle"));

        // While local is still Connecting (no snapshot yet), twin is unproven; once the Connected
        // frame's snapshot lands, the twin's remote row is suppressed immediately without waiting
        // for the trailing Status flip.
        var recording = false;
        var sawRemoteTwin = false;
        using var sub = dir.Rows.Connect().Subscribe(_ => {
            if (recording && dir.Rows.Lookup("remote:a1").HasValue) sawRemoteTwin = true;
        });

        recording = true;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsFalse();

        local.Agents.AddOrUpdate(LocalAgent("a1"));
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));

        await Assert.That(sawRemoteTwin).IsFalse();
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsFalse();
    }

    // ── the server lane ────────────────────────────────────────────────────────────────────────

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerSessionsService Sessions, FakeTimeProvider Time, AgentDirectory Dir)
        BuildWithSessions() {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var sessions = new FakeServerSessionsService();
        var time = new FakeTimeProvider();
        var dir = new AgentDirectory(
            local, remote, new FakeServerLane(), new RepoIdentityResolver(_ => null), p => p,
            "m1", Server, time, sessions);
        return (local, remote, sessions, time, dir);
    }

    static RepoSessionDto Listed(string id, string status = "ended", string owner = "me", string ownerName = "Alexey") => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = id,
        Owner = new() { UserId = owner, DisplayName = ownerName, AvatarUrl = $"https://avatars.example/{owner}" },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch : null,
    };

    /// The local daemon connected and reporting the app's own server.
    static void LocalConnected(FakeDaemonClientService local) {
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
    }

    /// A live session of the viewer's: in the unfiltered listing and in the Mine one.
    static void MineLive(FakeServerSessionsService sessions, string id) {
        var row = Listed(id, "active");
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);
    }

    /// A registry read that fails or a lane blip makes the registry stale, not the session ended:
    /// while the listing says live, the retained row stands and the server row stays hidden.
    [Test]
    public async Task A_stale_registry_row_stays_while_the_listing_says_live() {
        var (_, remote, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        remote.Cache.AddOrUpdate(Remote("r1", daemon: "elsewhere", owner: "me", sessionId: "S"));
        MineLive(sessions, "S");
        await Assert.That(dir.Rows.Lookup("remote:r1").HasValue).IsTrue();

        remote.CurrentSubject.OnNext(false);

        await Assert.That(dir.Rows.Lookup("remote:r1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
    }

    /// The registry names the owner before the listing names the session: the viewer's own agent on
    /// another machine shows under Mine from its first row, a teammate's waits for the listing.
    [Test]
    public async Task An_unlisted_registry_row_of_the_viewers_stays_under_Mine() {
        var (_, remote, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        sessions.ViewerIdsSubject.OnNext(new HashSet<string>(StringComparer.Ordinal) { "me" });
        remote.Cache.AddOrUpdate(Remote("r1", daemon: "elsewhere", owner: "me"));
        remote.Cache.AddOrUpdate(Remote("r2", daemon: "elsewhere", owner: "u2"));

        await Assert.That(dir.Rows.Lookup("remote:r1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:r2").HasValue).IsFalse();
    }

    /// A daemon row knows when its agent registered, not when it last did anything; the listing does.
    [Test]
    public async Task A_hosted_row_takes_its_listings_activity_time() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        var row = Listed("S", "active") with { LastActivityAt = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero) };
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);

        await Assert.That(dir.Rows.Lookup("local:a1").Value.LastActivityAt).IsEqualTo(new DateTime(2026, 10, 24, 9, 30, 0, DateTimeKind.Utc));
    }

    /// The tone cache marks a hosted row from its listing, so the daemon row carries the listing's PR links.
    [Test]
    public async Task A_hosted_row_takes_its_listings_pull_requests() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        var prs = new RepoSessionPullRequests([new() { RepoHash = "h", Owner = "o", RepoName = "r", Number = 7, State = "open" }]);
        var row = Listed("S", "active") with { PullRequests = prs };
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);

        await Assert.That(dir.Rows.Lookup("local:a1").Value.ListedPullRequests).IsEqualTo(prs);
    }

    /// A hosted row from another machine has no worktree here, so the listing's branch is all its
    /// PR readers have to find a PR opened since the listing.
    [Test]
    public async Task A_hosted_row_takes_its_listings_branch() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        var row = Listed("S", "active") with { Branch = "feature/x" };
        sessions.LiveCache.AddOrUpdate(row);
        sessions.MineLiveCache.AddOrUpdate(row);

        await Assert.That(dir.Rows.Lookup("local:a1").Value.Branch).IsEqualTo("feature/x");
    }

    /// A session the server lists with no daemon behind it has no agent here to answer for it: its
    /// permission requests get no card and no pip.
    [Test]
    public async Task A_server_row_is_not_a_session_agent() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S1"));
        MineLive(sessions, "S2");
        IReadOnlyDictionary<string, string>? map = null;
        using var sub = dir.SessionAgents.Subscribe(m => map = m);

        await Assert.That(map!["S1"]).IsEqualTo("a1");
        await Assert.That(map.ContainsKey("S2")).IsFalse();
    }

    /// The daemon reports a hook's GUID as spelled; the server files it as 32 hex digits. One session, one row.
    [Test]
    public async Task A_local_row_matches_its_server_row_whatever_the_guid_spelling() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "0D1E2F30-4455-6677-8899-AABBCCDDEEFF"));
        MineLive(sessions, "0d1e2f30445566778899aabbccddeeff");

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:0d1e2f30445566778899aabbccddeeff").HasValue).IsFalse();
    }

    [Test]
    public async Task Server_rows_appear_beside_daemon_rows() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        sessions.EndedCache.AddOrUpdate(Listed("S1"));
        MineLive(sessions, "S2");

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S1").Value.IsEndedSession).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S2").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S2").Value.IsTerminalSession).IsTrue();
    }

    [Test]
    public async Task A_live_local_row_hides_the_server_row_for_its_session() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        MineLive(sessions, "S");

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
    }

    [Test]
    public async Task A_completed_local_row_yields_to_the_server_row() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Completed"));
        sessions.EndedCache.AddOrUpdate(Listed("S"));

        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsFalse();
    }

    [Test]
    public async Task A_failed_local_row_keeps_hiding_the_server_row() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Failed"));
        sessions.EndedCache.AddOrUpdate(Listed("S"));

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
    }

    [Test]
    public async Task A_cached_local_row_yields_to_an_ended_server_row_and_keeps_an_active_one_hidden() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        sessions.EndedCache.AddOrUpdate(Listed("S"));
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsFalse();

        sessions.EndedCache.RemoveKey("S");
        MineLive(sessions, "S");
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
    }

    [Test]
    public async Task A_retained_registry_row_yields_to_the_server_row() {
        var (_, remote, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        remote.CurrentSubject.OnNext(false);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", sessionId: "S"));
        sessions.EndedCache.AddOrUpdate(Listed("S"));

        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();

        remote.CurrentSubject.OnNext(true);
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();
    }

    [Test]
    public async Task A_server_row_keeps_hosted_presentation_within_the_grace() {
        var (local, _, sessions, time, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        MineLive(sessions, "S");
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsFalse();

        local.Agents.RemoveKey("a1");
        await Assert.That(dir.Rows.Lookup("server:S").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:S").Value.IsTerminalSession).IsFalse();

        time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
        await Assert.That(dir.Rows.Lookup("server:S").Value.IsTerminalSession).IsTrue();
    }

    /// Stop completes the local row before the listing stops calling the session active. Within the
    /// grace the daemon's word stands, so the rail does not say Running beside a Done header; past
    /// it the listing's answer is the one shown.
    [Test]
    public async Task A_local_row_that_completes_shows_its_session_ended_while_the_listing_catches_up() {
        var (local, _, sessions, time, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S"));
        MineLive(sessions, "S");

        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Completed"));

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsFalse();
        await Assert.That(dir.Rows.Lookup("server:S").Value.IsEndedSession).IsTrue();

        // The daemon can drop the completed row before the listing catches up.
        local.Agents.RemoveKey("a1");
        await Assert.That(dir.Rows.Lookup("server:S").Value.IsEndedSession).IsTrue();

        time.Advance(AgentDirectory.HandoverGrace + TimeSpan.FromSeconds(1));
        await Assert.That(dir.Rows.Lookup("server:S").Value.IsEndedSession).IsFalse();
    }

    /// A row the daemon never saw live proves nothing about the session's end.
    [Test]
    public async Task A_completed_local_row_outside_the_grace_leaves_an_active_listing_alone() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S", status: "Completed"));
        MineLive(sessions, "S");

        await Assert.That(dir.Rows.Lookup("server:S").Value.IsEndedSession).IsFalse();
    }

    /// The listing names only a cwd, which for a worktree agent is the worktree; the row that
    /// replaces the daemon's keeps the checkout the daemon reported, after the daemon forgets it too.
    [Test]
    public async Task The_server_row_that_replaces_a_daemon_row_keeps_its_checkout() {
        var (local, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        local.Agents.AddOrUpdate(LocalAgent("a1", sessionId: "S") with { WorktreePath = "/r/.wt/a1", WorkLocation = "owned" });
        sessions.LiveCache.AddOrUpdate(Listed("S", "active") with { Cwd = "/r/.wt/a1", Branch = "feature" });
        sessions.MineLiveCache.AddOrUpdate(Listed("S", "active") with { Cwd = "/r/.wt/a1", Branch = "feature" });

        local.Agents.RemoveKey("a1");
        sessions.LiveCache.RemoveKey("S");
        sessions.MineLiveCache.RemoveKey("S");
        sessions.EndedCache.AddOrUpdate(Listed("S") with { Cwd = "/r/.wt/a1" });

        var row = dir.Rows.Lookup("server:S").Value;
        await Assert.That(row.RepoPath).IsEqualTo("/r");
        await Assert.That(row.WorktreePath).IsEqualTo("/r/.wt/a1");
        await Assert.That(row.WorkLocation).IsEqualTo("owned");
        await Assert.That(row.Branch).IsEqualTo("feature");
    }

    [Test]
    public async Task Mine_hides_a_teammates_hosted_row_and_keeps_unknown_local_rows() {
        var (local, remote, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        sessions.LiveCache.AddOrUpdate(Listed("S1", "active", owner: "other", ownerName: "Tony Young"));
        MineLive(sessions, "S2");
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "other", sessionId: "S1"));
        remote.Cache.AddOrUpdate(Remote("b2", daemon: "home-pc", owner: "other", sessionId: "S9"));
        remote.Cache.AddOrUpdate(Remote("b3", daemon: "home-pc", sessionId: "S2"));
        local.Agents.AddOrUpdate(LocalAgent("a2"));
        local.Pending.AddOrUpdate(new PendingLaunchDto("p1", "claude", "/r", "Launching", DateTime.UtcNow, "spawned"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse(); // the listing names another owner
        await Assert.That(dir.Rows.Lookup("remote:b2").HasValue).IsFalse(); // not listed: unknown on the registry lane
        await Assert.That(dir.Rows.Lookup("remote:b3").HasValue).IsTrue();  // the viewer's own
        await Assert.That(dir.Rows.Lookup("local:a2").HasValue).IsTrue();   // not listed: presumed the viewer's
        await Assert.That(dir.Rows.Lookup("pending:p1").HasValue).IsTrue();

        sessions.Scope = RailScope.Team;
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b2").HasValue).IsTrue();
    }

    [Test]
    public async Task Ownership_comes_from_the_scoped_listing_without_owner_ids() {
        var (local, remote, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        LocalConnected(local);
        sessions.LiveCache.AddOrUpdate(Listed("S1", "active", owner: "other", ownerName: "Tony Young"));
        MineLive(sessions, "S2");
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "home-pc", owner: "other", sessionId: "S1"));
        local.Agents.AddOrUpdate(LocalAgent("a2", sessionId: "S2"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();
        var mine = dir.Rows.Lookup("local:a2").Value;
        await Assert.That(mine.OwnerIsViewer).IsTrue();
        await Assert.That(mine.OwnerName).IsEqualTo("Alexey");
        await Assert.That(mine.OwnerUserId).IsEqualTo("me");

        sessions.Scope = RailScope.Team;
        var theirs = dir.Rows.Lookup("remote:b1").Value;
        await Assert.That(theirs.OwnerName).IsEqualTo("Tony Young");
        await Assert.That(theirs.OwnerAvatarUrl).IsEqualTo("https://avatars.example/other");
        await Assert.That(theirs.OwnerIsViewer).IsNull();
        sessions.ViewerIdsSubject.OnNext(new HashSet<string>(["me"]));
        await Assert.That(dir.Rows.Lookup("remote:b1").Value.OwnerIsViewer).IsFalse();
    }

    [Test]
    public async Task Server_keys_are_not_normalised() {
        var (_, _, sessions, _, dir) = BuildWithSessions();
        using var _d = dir;
        using var _s = sessions;
        sessions.EndedCache.AddOrUpdate(Listed("ABC"));
        sessions.EndedCache.AddOrUpdate(Listed("abc"));

        await Assert.That(dir.Rows.Lookup("server:ABC").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("server:abc").HasValue).IsTrue();
    }
}
