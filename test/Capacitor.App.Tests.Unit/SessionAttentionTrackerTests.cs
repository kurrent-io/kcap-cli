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
        public int Fetches;

        public Harness() {
            Tracker = new SessionAttentionTracker(Lane, (sid, _) => { Fetches++; return Task.FromResult(Detail(sid)); }, Time, TimeSpan.FromMilliseconds(100));
            Tracker.SessionsWithAttention.Subscribe(s => Sessions = s);
        }

        public void Connect(int epoch = 1) => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        public void Dispose() => Tracker.Dispose();
    }

    static SessionDetailFetch Pending(params string[] ids) {
        var events = string.Join(",", ids.Select((id, i) => $$$"""{"event_type":"InterruptIssued","event_number":{{{i}}},"payload":{"request_id":"{{{id}}}","kind":"permission","tool_name":"Bash"}}"""));
        return new(System.Text.Json.JsonSerializer.Deserialize($$"""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":[{{events}}]}""", RemoteModelsJsonContext.Default.SessionDetailDto));
    }

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
}
