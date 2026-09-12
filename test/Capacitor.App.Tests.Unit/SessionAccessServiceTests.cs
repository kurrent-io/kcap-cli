using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionAccessServiceTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAccessService Service;
        public Harness() => Service = new SessionAccessService(Lane, Time);
        public void Connect(int epoch = 1) => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "closed"));
        public static async Task<SessionAccessState> Current(SessionAccessLease lease) {
            SessionAccessState? state = null;
            using (lease.State.Subscribe(s => state = s)) { }
            return state!.Value;
        }
        public void Dispose() => Service.Dispose();
    }

    [Test]
    public async Task Establishes_in_order_watch_then_chat_and_reports_established() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "chat subscribe");
        await Assert.That(h.Lane.Calls.IndexOf("watch:s1")).IsLessThan(h.Lane.Calls.IndexOf("chat:s1"));
        await Assert.That(h.Lane.AccessWatches).Contains("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        lease.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }

    [Test]
    public async Task A_denied_watch_is_terminal_and_never_subscribes_chat() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied");
        await Assert.That(h.Lane.ChatSubscribes).DoesNotContain("s1");
    }

    [Test]
    public async Task Lane_down_is_unavailable_and_reconnect_re_establishes() {
        using var h = new Harness();
        var lease = h.Service.Acquire("s1");
        await Assert.That(await Harness.Current(lease)).IsEqualTo(SessionAccessState.Unavailable);
        h.Connect();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established after connect");
        h.Drop();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable on drop");
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Count(s => s == "s1") == 2, what: "re-subscribed after reconnect");
    }

    [Test]
    public async Task Access_changed_ping_rechecks_and_a_denial_moves_the_lease_to_denied() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied after recheck");
    }

    [Test]
    public async Task A_transient_failure_retries_on_the_ladder_while_connected() {
        using var h = new Harness();
        var attempts = 0;
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(++attempts == 1 ? HubCallOutcome.Failed("recheck") : HubCallOutcome.Ok);
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable after the failure");

        // The ladder arms its timer just AFTER publishing Unavailable, so a single advance placed
        // between the two fires nothing and the retry never comes. Advancing on each poll is
        // idempotent -- a no-op until the timer exists, and it fires on the first poll after.
        await WaitUntilAsync(async () => {
            h.Time.Advance(TimeSpan.FromSeconds(2));
            return await Harness.Current(lease) == SessionAccessState.Established;
        }, "established on retry");
    }

    [Test]
    public async Task Two_leases_share_one_subscription_and_unsubscribe_on_the_last_release() {
        using var h = new Harness();
        h.Connect();
        var a = h.Service.Acquire("s1");
        var b = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(b) == SessionAccessState.Established, "established");
        await Assert.That(h.Lane.ChatSubscribes.Count(s => s == "s1")).IsEqualTo(1);
        a.Dispose();
        await Task.Delay(50);
        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
        b.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }

    /// The give-back is ordered behind the join it undoes, so the membership ends up dropped
    /// whichever way the two hub calls would otherwise have raced each other to the server.
    [Test]
    public async Task Lease_disposed_while_chat_subscribe_in_flight_still_unsubscribes() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>();
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "chat subscribe started");
        lease.Dispose();
        gate.SetResult(HubCallOutcome.Ok);
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "the membership given back once the subscribe resolved");
    }

    /// Closing and reopening the same workspace while the first attempt is still in flight. The
    /// stale attempt did join, and handing that group back while the reopened lease receives
    /// payloads on it leaves the lease Established, with no retry armed, while nothing arrives.
    /// Ordering the calls is what settles it: by the time the stale give-back can run, the
    /// reopened entry holds the session and neither give-back applies.
    [Test]
    public async Task A_stale_attempt_leaves_the_chat_a_live_lease_holds_alone() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var first = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "the first chat subscribe");

        first.Dispose();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        using var live = h.Service.Acquire("s1");
        gate.SetResult(HubCallOutcome.Ok);

        await WaitUntilAsync(async () => await Harness.Current(live) == SessionAccessState.Established, "the reopened lease established");
        await Task.Delay(100); // the stale attempt's give-back, were it not dropped, lands here

        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
        await Assert.That(await Harness.Current(live)).IsEqualTo(SessionAccessState.Established);
    }

    /// The same race on the release path: the last lease goes while a fresh acquisition is
    /// establishing, and the release's own give-back must find the session claimed again and
    /// leave the membership alone. The first attempt never joined, so nothing else can hand it
    /// back and the session has to end up subscribed.
    [Test]
    public async Task A_release_racing_a_fresh_acquisition_leaves_the_session_subscribed() {
        using var h = new Harness();
        var gate = new TaskCompletionSource<HubCallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Lane.SubscribeChatHandler = _ => gate.Task;
        h.Connect();
        var first = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "the first chat subscribe");

        first.Dispose();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
        using var live = h.Service.Acquire("s1");
        gate.SetResult(HubCallOutcome.Failed("closed"));

        await WaitUntilAsync(async () => await Harness.Current(live) == SessionAccessState.Established, "the reopened lease established");
        await Task.Delay(100); // the release's give-back, were it not dropped, lands here

        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
    }

    /// A hub failure is otherwise indistinguishable from the lane being down: the state is
    /// Unavailable either way, and the retry ladder keeps trying in silence. The reason is
    /// reported once per entry, not once per retry tick.
    [Test]
    [NotInParallel]
    public async Task A_failed_subscribe_reports_the_reason_once() {
        using var capture = ConsoleOutput.StartErrorCapture(newLine: "\n");
        using var h = new Harness();
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Failed("boom"));
        h.Connect();
        using var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable after the failure");

        // Same arming gap as the ladder test: advance on each poll rather than once. Every attempt
        // fails here, so a later rung can arm and fire too -- the count is a floor, and the dedup
        // this test is about holds however many retries ran, since the reason never changes.
        await WaitUntilAsync(() => {
            h.Time.Advance(TimeSpan.FromSeconds(2));
            return h.Lane.ChatSubscribes.Count(s => s == "s1") >= 2;
        }, what: "the retry");
        await Task.Delay(100); // the second attempt's report, were it not deduped, lands here

        var lines = capture.GetCapturedError().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("s1").And.Contains("boom");
    }

    [Test]
    public async Task Dispose_ignores_late_lane_notifications_and_completes_leases() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        var callsBeforeDispose = h.Lane.Calls.Count;
        h.Service.Dispose();

        var completed = false;
        using (lease.State.Subscribe(_ => { }, () => completed = true)) { }
        await Assert.That(completed).IsTrue();

        h.Connect(epoch: 2);
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await Task.Delay(50);
        await Assert.That(h.Lane.Calls.Count).IsEqualTo(callsBeforeDispose);
    }
}
