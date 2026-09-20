using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionAttentionTrackerTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAttentionTracker Tracker;
        public IReadOnlySet<string> Sessions = new HashSet<string>();
        public Func<string, SessionDetailFetch> Detail = _ => new(null, NotFound: true);
        /// Set instead of Detail when the test needs to hold the fetch open.
        public Func<string, Task<SessionDetailFetch>>? DetailTask;
        public int Fetches;

        public Harness() {
            Tracker = new SessionAttentionTracker(Lane, (sid, _) => { Fetches++; return DetailTask?.Invoke(sid) ?? Task.FromResult(Detail(sid)); }, Time, TimeSpan.FromMilliseconds(100));
            Tracker.SessionsWithAttention.Subscribe(s => Sessions = s);
        }

        public void Connect(int epoch = 1, string subject = "u1") => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        public void Dispose() => Tracker.Dispose();
    }

    static SessionDetailFetch Pending(params string[] ids) => Snapshot(ids.Select(Permission));

    static string Permission(string id) =>
        $$$"""{"event_type":"InterruptIssued","event_number":0,"payload":{"request_id":"{{{id}}}","kind":"permission","tool_name":"Bash"}}""";

    /// A Claude AskUserQuestion as the transcript pipeline records it: under the transcript entry's
    /// own id, which no response ping ever carries.
    static string Question(string id) =>
        $$$"""{"event_type":"InterruptIssued","event_number":0,"payload":{"request_id":"{{{id}}}","kind":"input","tool_name":"AskUserQuestion","prompt":"Which?"}}""";

    static SessionDetailFetch Snapshot(IEnumerable<string> events) =>
        new(System.Text.Json.JsonSerializer.Deserialize($$"""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":[{{string.Join(",", events)}}]}""", RemoteModelsJsonContext.Default.SessionDetailDto));

    [Test]
    public async Task Two_prompts_need_two_responses_before_the_pip_clears() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1", "r2");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await Assert.That(h.Sessions).Contains("s1");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r2"));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "attention off");
    }

    [Test]
    public async Task A_ping_before_the_first_reconciliation_completes_survives_a_disconnect() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Drop(); // before the debounce fires
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(50);
        await Assert.That(h.Fetches).IsEqualTo(0);
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "dirty session reconciled after reconnect");
    }

    [Test]
    public async Task A_response_missed_while_disconnected_clears_on_reconnect() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Drop();
        h.Detail = _ => Pending();
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared by re-reconciliation");
    }

    [Test]
    public async Task A_transient_failure_retries_to_an_authoritative_result() {
        using var h = new Harness();
        h.Connect();
        var calls = 0;
        h.Detail = _ => ++calls == 1 ? new SessionDetailFetch(null) : Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 1, what: "first fetch");
        await Assert.That(h.Sessions).DoesNotContain("s1");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "retry succeeded");
    }

    /// The response is newer than the snapshot the reconciliation already holds, so the ids it
    /// settled must not come back with that snapshot.
    [Test]
    public async Task A_response_supersedes_a_reconciliation_that_started_before_it() {
        using var h = new Harness();
        h.Connect();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.DetailTask = async _ => { await gate.Task; return Pending("r1"); };
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 1, what: "the fetch");

        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", null));
        gate.SetResult();
        await Task.Delay(100);

        await Assert.That(h.Sessions).DoesNotContain("s1");
    }

    /// The reconciliation a reconnect schedules is owed to the set that outlived the disconnect, so
    /// a response superseding it has to leave another one armed rather than a stale set.
    [Test]
    public async Task A_response_racing_the_reconnects_reconciliation_leaves_one_still_owed() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1", "r2");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");

        h.Drop();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.DetailTask = async _ => { await gate.Task; return Pending("r1", "r2"); };
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Fetches == 2, what: "the reconnect's reconciliation");

        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        gate.SetResult();
        h.DetailTask = null;
        h.Detail = _ => Pending();
        h.Time.Advance(TimeSpan.FromMilliseconds(100));

        await WaitUntilAsync(() => h.Fetches == 3, what: "the re-armed reconciliation");
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "the fetched snapshot applied");
    }

    /// Signing in as another account must leave none of the previous user's pips on screen, and
    /// the fetch that account started must not put them back. The lane stays Connected across the
    /// change, so nothing about the connection itself reports it.
    [Test]
    public async Task An_identity_change_drops_every_session_and_the_fetch_it_started() {
        using var h = new Harness();
        h.Connect(subject: "u1");
        h.Detail = _ => Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on under u1");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.DetailTask = async _ => { await gate.Task; return Pending("r1", "r2"); };
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "u1's fetch in flight");

        h.Connect(subject: "u2");
        await Assert.That(h.Sessions).IsEmpty();

        gate.SetResult();
        await Task.Delay(100);
        await Assert.That(h.Sessions).IsEmpty();
    }

    [Test]
    public async Task A_response_naming_an_unknown_id_re_reconciles() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("transcript-1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Detail = _ => Pending();
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "tracker-9"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared by the re-reconciliation");
    }

    /// The stream resolves a transcript question only once the agent's tool result is ingested,
    /// which trails the response ping: the snapshot fetched in between still lists it as open.
    [Test]
    public async Task A_question_clears_on_its_answer_though_the_stream_still_lists_it() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Snapshot([Question("t1")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");

        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "hook-1"));
        await Assert.That(h.Sessions).DoesNotContain("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "the re-reconciliation");
        await Assert.That(h.Sessions).DoesNotContain("s1");
    }

    /// The question's own record can trail its ping too, so the first snapshot misses it and the
    /// one the answer triggers is the first to show it — already answered.
    [Test]
    public async Task A_question_first_listed_after_its_answer_never_lights() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending();
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 1, what: "the first reconciliation");

        h.Detail = _ => Snapshot([Question("t1")]);
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "hook-1"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "the re-reconciliation");
        await Assert.That(h.Sessions).DoesNotContain("s1");
    }

    /// A settled question whose resolution the stream never records must not ride back in on the
    /// next prompt's snapshot and outlive that prompt.
    [Test]
    public async Task A_settled_question_stays_settled_in_later_snapshots() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Snapshot([Question("t1")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "hook-1"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "the re-reconciliation");

        h.Detail = _ => Snapshot([Question("t1"), Permission("r1")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "the new prompt");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared with the prompt");
    }

    [Test]
    public async Task A_new_question_lights_after_an_earlier_one_was_settled() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Snapshot([Question("t1")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "hook-1"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "the re-reconciliation");

        h.Detail = _ => Snapshot([Question("t2")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "the second question");
    }

    /// An unplaced response settles questions only: a permission it did not name is still open.
    [Test]
    public async Task An_answered_question_leaves_an_open_permission_lit() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Snapshot([Question("t1"), Permission("r1")]);
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");

        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "hook-1"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 2, what: "the re-reconciliation");
        await Assert.That(h.Sessions).Contains("s1");

        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared with the permission");
    }
}
