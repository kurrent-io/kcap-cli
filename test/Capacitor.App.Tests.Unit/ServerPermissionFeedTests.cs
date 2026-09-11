using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ServerPermissionFeedTests {
    sealed class Harness : IDisposable {
        int _fetches;

        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAccessService Access;
        public readonly PermissionService Permissions;
        public readonly IObservableCache<PendingPermissionRequest, string> View;
        public readonly ServerPermissionFeed Feed;
        public Func<string, Task<SessionDetailFetch>> Detail = _ => Task.FromResult(new SessionDetailFetch(null, NotFound: true));

        public Harness() {
            Access = new SessionAccessService(Lane, Time);
            Permissions = new PermissionService(new FakeDaemonClientService(), new ScriptedLocalControlOps(),
                _ => AsyncEnumerable.Empty<PermissionStreamEvent>(), Time, CancellationToken.None);
            View = Permissions.Pending.AsObservableCache();
            Feed = new ServerPermissionFeed(Lane, Access, Permissions,
                (sid, _) => { Interlocked.Increment(ref _fetches); return Detail(sid); }, _ => "claude", Time);
        }

        public int Fetches => Volatile.Read(ref _fetches);

        public void Connect(string subject = "u1") => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: 1));

        public void Dispose() { Feed.Dispose(); Access.Dispose(); Permissions.Dispose(); View.Dispose(); }
    }

    static SessionAccessState Current(SessionAccessLease lease) {
        SessionAccessState? state = null;
        using (lease.State.Subscribe(s => state = s)) { }
        return state!.Value;
    }

    static SessionDetailFetch DetailWith(string eventsJson) => new(JsonSerializer.Deserialize(
        $$"""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":{{eventsJson}}}""", RemoteModelsJsonContext.Default.SessionDetailDto));

    [Test]
    public async Task Live_pushes_become_server_items_and_a_responded_ping_settles_them() {
        using var h = new Harness();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Lane.ElicitationsSubject.OnNext(new ServerElicitationRequest("s1", "q1", "Pick", [new AcpInteractionOption { OptionId = "a", Label = "A" }], false));
        await Assert.That(h.View.Count).IsEqualTo(2);
        await Assert.That(h.View.Lookup("server:r1").Value.Vendor).IsEqualTo("claude");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", null));
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Establishing_a_session_reconciles_its_cards_and_transcript_questions_get_none() {
        using var h = new Harness();
        h.Detail = _ => Task.FromResult(DetailWith("""
            [
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}},
            {"event_type":"InterruptIssued","event_number":2,"payload":{"request_id":"t1","kind":"input","tool_name":"AskUserQuestion","prompt":"?"}}
            ]
            """));
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Lookup("server:p1").HasValue, what: "reconciled card");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    /// A permission-kind interrupt naming the elicitation tool is still a question: answered as a
    /// generic Allow it would settle with no updated_input, so the reconciled card has to classify
    /// it the way the live push does.
    [Test]
    public async Task A_reconciled_permission_kind_elicitation_is_still_a_question() {
        using var h = new Harness();
        h.Detail = _ => Task.FromResult(DetailWith("""
            [
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"q1","kind":"permission","tool_name":"AskUserQuestion","extensions":{"claude_code":{"permission":{"tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}}}}}}
            ]
            """));
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Lookup("server:q1").HasValue, what: "the reconciled card");
        await Assert.That(h.View.Lookup("server:q1").Value.IsQuestion).IsTrue();
    }

    [Test]
    public async Task A_settlement_racing_the_fetch_wins() {
        using var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Detail = async _ => {
            await gate.Task;
            return DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]""");
        };
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "fetch started");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", null));
        gate.SetResult();
        await Task.Delay(100);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    /// The fetch's own snapshot still lists the cards access was revoked over, so only the
    /// generation stops the reconciliation from handing them back.
    [Test]
    public async Task A_denial_racing_the_fetch_wins() {
        using var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Detail = async _ => {
            await gate.Task;
            return DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]""");
        };
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "p1", "Bash", null, null));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "fetch started");

        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cards dropped on denial");

        gate.SetResult();
        await Task.Delay(100);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    /// A push that lands after the snapshot was taken is newer than it, so its absence from the
    /// snapshot is not evidence of anything.
    [Test]
    public async Task A_push_racing_the_fetch_outlives_the_snapshot() {
        using var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Detail = async _ => {
            await gate.Task;
            return DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]""");
        };
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "fetch started");

        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r2", "Bash", null, null));
        gate.SetResult();
        await WaitUntilAsync(() => h.View.Lookup("server:p1").HasValue, what: "the snapshot applied");

        await Assert.That(h.View.Lookup("server:r2").HasValue).IsTrue();
    }

    [Test]
    public async Task Denial_drops_the_sessions_cards_without_settling_them() {
        using var h = new Harness();
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cards dropped on denial");
        // The same id landing again proves the drop left no tombstone behind.
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    /// A denial speaks only for the attempt it was raised under. Held behind a grant that replaced
    /// it, its drop would otherwise delete cards that grant's own reconciliation proved pending.
    [Test]
    public async Task A_superseded_denial_leaves_the_grant_that_replaced_it_alone() {
        using var h = new Harness();
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "p1", "Bash", null, null));

        using var held = await h.Feed.CommitGates.EnterAsync("s1");
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => Current(lease) == SessionAccessState.Denied, what: "the denial");
        await Task.Delay(100); // the drop, were it not ordered, lands here
        await Assert.That(h.View.Lookup("server:p1").HasValue).IsTrue();

        // Access comes back, and the grant's own fetch is authoritative that the card is pending.
        h.Detail = _ => Task.FromResult(DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]"""));
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "the grant's fetch");

        held.Dispose();

        await Task.Delay(100); // the superseded drop resumes here, behind the grant
        await Assert.That(h.View.Lookup("server:p1").HasValue).IsTrue();
    }

    /// A fetch that merely failed is not evidence the session has no cards — only a 404 is.
    [Test]
    public async Task A_failed_fetch_leaves_the_sessions_cards_in_place() {
        using var h = new Harness();
        h.Detail = _ => Task.FromResult(new SessionDetailFetch(null, NotFound: false));
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "the fetch");
        await Task.Delay(100);
        await Assert.That(h.View.Lookup("server:r1").HasValue).IsTrue();
    }

    [Test]
    public async Task An_ended_session_reconciles_to_no_cards() {
        using var h = new Harness();
        h.Detail = _ => Task.FromResult(new SessionDetailFetch(JsonSerializer.Deserialize(
            """
            {"session_id":"s1","ended_at":"2026-09-10T10:00:00Z","last_event_number":1,
             "events":[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"r1","kind":"permission","tool_name":"Bash"}}]}
            """, RemoteModelsJsonContext.Default.SessionDetailDto)));
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cards replaced with none");
    }

    /// A session with nothing cached has no generation of its own to move, so only the lane epoch
    /// can tell the previous account's fetch that it is answering for someone else.
    [Test]
    public async Task An_identity_change_racing_a_fetch_for_an_empty_session_wins() {
        using var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Detail = async _ => {
            await gate.Task;
            return DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]""");
        };
        h.Connect("u1");
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "fetch started");

        h.Connect("u2");
        gate.SetResult();
        await Task.Delay(100);

        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    /// A same-user reconnect moves neither the session's generation nor the lane epoch, and the
    /// live sequence only protects newer additions — so only the attempt can stop the superseded
    /// fetch from handing back the card the reconnect's own reconciliation removed.
    [Test]
    public async Task A_superseded_fetch_cannot_restore_what_the_reconnects_reconciliation_removed() {
        using var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stillListsIt = DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]""");
        h.Detail = async _ => { await gate.Task; return stillListsIt; };
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "p1", "Bash", null, null));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "the first fetch");
        await Assert.That(h.View.Lookup("server:p1").HasValue).IsTrue();

        // The drop and reconnect re-establish access as the SAME user, and the fetch that follows
        // is authoritative that the request settled while the hub was down.
        h.Detail = _ => Task.FromResult(DetailWith("[]"));
        h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        h.Connect();
        await WaitUntilAsync(() => !h.View.Lookup("server:p1").HasValue, what: "the reconnect's fetch removed the card");

        gate.SetResult();
        await Task.Delay(100);

        await Assert.That(h.View.Lookup("server:p1").HasValue).IsFalse();
    }

    /// Testing the attempt and committing the result are one unit. Held between the two, a fetch
    /// resumes into a cache the attempt that replaced it has already reconciled, and hands back
    /// what that attempt proved gone — a same-user reconnect moves neither the cache generation
    /// nor the lane epoch, so ordering is all that separates the two commits.
    [Test]
    public async Task A_fetch_held_at_its_commit_cannot_outlive_the_attempt_that_replaced_it() {
        using var h = new Harness();
        h.Detail = _ => Task.FromResult(DetailWith("""
            [
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}},
            {"event_type":"InterruptIssued","event_number":2,"payload":{"request_id":"p2","kind":"permission","tool_name":"Bash"}}
            ]
            """));
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "p1", "Bash", null, null));

        using var held = await h.Feed.CommitGates.EnterAsync("s1");
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "the first fetch");
        await Task.Delay(100); // the commit, were it not ordered, lands here
        // p2 is this fetch's alone, so its absence is what says the commit has not run yet.
        await Assert.That(h.View.Lookup("server:p2").HasValue).IsFalse();

        // The reconnect's own fetch is authoritative that both settled while the hub was down.
        h.Detail = _ => Task.FromResult(DetailWith("[]"));
        h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        h.Connect();
        await WaitUntilAsync(() => h.Fetches == 2, what: "the reconnect's fetch");

        held.Dispose();

        await WaitUntilAsync(() => !h.View.Lookup("server:p1").HasValue, what: "the reconnect's fetch removed the card");
        await Assert.That(h.View.Lookup("server:p2").HasValue).IsFalse();
    }

    [Test]
    public async Task An_identity_change_clears_the_server_lane() {
        using var h = new Harness();
        h.Connect("u1");
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Connect("u2");
        await Assert.That(h.View.Count).IsEqualTo(0);
    }
}
