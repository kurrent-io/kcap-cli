using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The terminal: it asks the server on the pane's first showing and again only when the answer
/// could have changed, the server's answer is the verdict, it is sized by the source, its viewport
/// is reported while the pane is showing and released when it stops driving, and each subscribe
/// lands on a fresh surface.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteTerminalViewModelTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<bool> Ended = new(false);
        public readonly BehaviorSubject<bool> Visible;
        public readonly List<FakeTerminalSurface> Surfaces = [];
        public readonly FakeTimeProvider Time = new();
        public readonly RemoteTerminalViewModel Vm;

        /// <param name="subscribe">The server's answers, in place before the pane's first showing asks.</param>
        public Harness(bool visible = true, Func<string, Task<HubCallOutcome>>? subscribe = null, bool measured = true) {
            if (subscribe is not null) Lane.TerminalSubscribeHandler = subscribe;
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Visible = new BehaviorSubject<bool>(visible);
            Vm = new RemoteTerminalViewModel("a1", Lane, Access, Ended, Visible, Time, () => { var s = new FakeTerminalSurface(); Surfaces.Add(s); return s; });
            if (measured) Vm.SetViewport(80, 24);
        }

        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        public void Reconnect() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));

        public static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    [Test]
    public async Task Subscribes_when_shown_then_shows_the_replay_and_live_frames() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes).IsEquivalentTo(new[] { "a1" });
            await Assert.That(h.Lane.Resizes).Contains(("a1", 80, 24));

            h.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 120, 40));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("hel")));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("other", Harness.B64("nope")));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("lo")));
            var surface = h.Surfaces.Single();
            await Assert.That(surface.Resizes).Contains((120, 40));
            await Assert.That(surface.Fed).IsEquivalentTo(new[] { "hel", "lo" });
            await Assert.That(h.Vm.SizeNote).IsEqualTo("120×40 · sized by the source");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_keystroke_that_is_a_wire_key_is_sent_and_anything_else_is_dropped() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var surface = h.Surfaces.Single();

            surface.RaiseInput([0x1b]);
            await WaitUntilAsync(() => h.Lane.SpecialKeys.Contains(("a1", SpecialKeys.Escape)), what: "escape");
            surface.RaiseInput("x"u8.ToArray());
            await h.Vm.SendKeyCommand.Execute(SpecialKeys.CtrlC);
            await Assert.That(h.Lane.SpecialKeys).IsEquivalentTo(new[] { ("a1", SpecialKeys.Escape), ("a1", SpecialKeys.CtrlC) });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task The_viewport_is_reported_while_live_and_released_on_teardown() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            h.Vm.SetViewport(100, 30);
            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 100, 30)), what: "the viewport");

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.TerminalUnsubscribes).Contains("a1");
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");
            await Assert.That(h.Vm.Surface).IsNull();
        });
    }

    [Test]
    public async Task A_reconnect_subscribes_again_onto_a_fresh_surface() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var first = h.Vm.Surface;

            h.Drop();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the viewport released");

            h.Reconnect();
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live again");
            await Assert.That(h.Vm.Surface).IsNotSameReferenceAs(first);
            await Assert.That(h.Surfaces.Count).IsEqualTo(2);
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_teardown_while_the_subscribe_is_in_flight_still_unsubscribes() {
        await RunOnUiAsync(async () => {
            var subscribeSource = new TaskCompletionSource<HubCallOutcome>();
            var h = new Harness(subscribe: _ => subscribeSource.Task);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Contains("a1"), what: "the subscribe call");

            await h.Vm.TeardownAsync();
            subscribeSource.SetResult(HubCallOutcome.Ok);
            await WaitUntilAsync(() => h.Lane.TerminalUnsubscribes.Contains("a1"), what: "the unsubscribe");
            // Nothing was reported through an in-flight subscribe, so there is no viewport of ours
            // in the server's aggregate to give back.
            await Assert.That(h.Lane.ResizeReleases).IsEmpty();
        });
    }

    [Test]
    public async Task An_unreachable_server_reads_as_offline_and_an_ended_session_as_ended() {
        await RunOnUiAsync(async () => {
            var h = new Harness(subscribe: _ => Task.FromResult(HubCallOutcome.NotConnected));
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");
            await Assert.That(h.Lane.Resizes).IsEmpty();

            h.Ended.OnNext(true);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Ended);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo("This session has ended.");
            h.Access.OnNext(SessionAccessState.Established);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Replay_frames_that_arrive_while_the_subscribe_is_in_flight_are_shown() {
        await RunOnUiAsync(async () => {
            var subscribeSource = new TaskCompletionSource<HubCallOutcome>();
            var h = new Harness(subscribe: _ => subscribeSource.Task);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Contains("a1"), what: "the subscribe call");

            h.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 120, 40));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("hel")));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("lo")));

            subscribeSource.SetResult(HubCallOutcome.Ok);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");

            var surface = h.Surfaces.Single();
            await Assert.That(surface.Fed).IsEquivalentTo(new[] { "hel", "lo" });
            await Assert.That(surface.Resizes).Contains((120, 40));
            await h.Vm.TeardownAsync();
        });
    }

    /// A workspace kept on its Chat tab costs the server no replay or stream: the pane subscribes on
    /// its first showing and stays subscribed when hidden again. Hidden, it reports no viewport — the
    /// server takes the smallest across viewers, so an unmeasured pane would clamp the agent's PTY.
    [Test]
    public async Task A_pane_subscribes_when_first_shown_and_holds_no_viewport_while_hidden() {
        await RunOnUiAsync(async () => {
            var h = new Harness(visible: false);
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalSubscribes).IsEmpty();

            h.Visible.OnNext(true);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes).IsEquivalentTo(new[] { "a1" });
            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 80, 24)), what: "the viewport");

            h.Visible.OnNext(false);
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the viewport released");
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(1);
        });
    }

    [Test]
    public async Task An_unmeasured_pane_never_reports_the_sources_size_as_its_viewport() {
        await RunOnUiAsync(async () => {
            var h = new Harness(measured: false);
            try {
                await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
                h.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 130, 54));
                await Assert.That(h.Lane.Resizes).IsEmpty();

                h.Vm.SetViewport(100, 30);
                await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 100, 30)), what: "the measured viewport");
                var reported = h.Lane.Resizes.Count;
                h.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 90, 25));
                h.Surfaces.Single().RaiseResize(90, 25);
                await Assert.That(h.Lane.Resizes.Count).IsEqualTo(reported);
                await Assert.That(h.Vm.SourceColumns).IsEqualTo(90);
                await Assert.That(h.Vm.SourceRows).IsEqualTo(25);

                h.Visible.OnNext(false);
                h.Visible.OnNext(true);
                await WaitUntilAsync(() => h.Lane.Resizes.Count > reported, what: "the restored viewport");
                await Assert.That(h.Lane.Resizes.Last()).IsEqualTo(("a1", 100, 30));
            } finally {
                await h.Vm.TeardownAsync();
            }
        });
    }

    /// The hub's bounds are 1..500 × 1..200, and (0,0) is its own clear sentinel.
    [Test]
    public async Task A_size_outside_the_servers_bounds_is_never_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var reported = h.Lane.Resizes.Count;

            h.Vm.SetViewport(0, 0);
            h.Vm.SetViewport(501, 30);
            h.Vm.SetViewport(100, 201);
            h.Vm.SetViewport(100, 30);

            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 100, 30)), what: "the viewport in bounds");
            await Assert.That(h.Lane.Resizes.Count).IsEqualTo(reported + 1);
            await h.Vm.TeardownAsync();
        });
    }

    /// Group membership is the connection's, not the subscription's: a stale attempt's release
    /// would deafen the pane the newer attach is already receiving on.
    [Test]
    public async Task A_stale_subscribe_resolving_behind_a_live_one_keeps_the_live_subscription() {
        await RunOnUiAsync(async () => {
            var stale = new TaskCompletionSource<HubCallOutcome>();
            var calls = 0;
            var h = new Harness(subscribe: _ => ++calls == 1 ? stale.Task : Task.FromResult(HubCallOutcome.Ok));
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 1, what: "the first subscribe");

            h.Drop();
            h.Reconnect();
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");

            stale.SetResult(HubCallOutcome.Ok);
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    /// <summary>The server says when it refuses the terminal, so the pane says so too, rather than reading
    /// as a connection that dropped; a failed subscribe stays offline.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_denied_subscribe_says_the_terminal_was_refused(bool denied) {
        await RunOnUiAsync(async () => {
            var h = new Harness(subscribe: _ => Task.FromResult(denied ? HubCallOutcome.Denied("no") : HubCallOutcome.Failed("boom")));
            var expected = denied ? RemoteTerminalPhase.Refused : RemoteTerminalPhase.Offline;
            await WaitUntilAsync(() => h.Vm.Phase == expected, what: expected.ToString());

            await Assert.That(h.Vm.PhaseNote).IsEqualTo(denied ? RemoteTerminalViewModel.RefusedNote : "Not connected to the session");
            await h.Vm.TeardownAsync();
        });
    }

    /// A stale subscribe the hub took while a newer one was in flight holds the connection in the
    /// group; if that newer one is then refused, the membership is given back.
    [Test]
    public async Task A_stale_subscribe_behind_a_refused_one_is_released() {
        await RunOnUiAsync(async () => {
            var stale = new TaskCompletionSource<HubCallOutcome>();
            var newer = new TaskCompletionSource<HubCallOutcome>();
            var calls = 0;
            var h = new Harness(subscribe: _ => ++calls == 1 ? stale.Task : newer.Task);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 1, what: "the first subscribe");
            h.Drop();
            h.Reconnect();
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");

            stale.SetResult(HubCallOutcome.Ok);
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();

            newer.SetResult(HubCallOutcome.Denied("no"));
            await WaitUntilAsync(() => h.Lane.TerminalUnsubscribes.Count == 1, what: "the stray membership released");
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Refused);
            await h.Vm.TeardownAsync();
        });
    }

    /// The special keys are the only input that crosses, and off Live there is nothing to send
    /// them to: the buttons say so instead of silently dropping them.
    [Test]
    public async Task The_key_command_is_offered_only_while_the_terminal_is_live() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsFalse();

            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsTrue();

            h.Drop();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_refused_subscribe_drops_later_frames() {
        await RunOnUiAsync(async () => {
            var h = new Harness(subscribe: _ => Task.FromResult(HubCallOutcome.Denied("x")));
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Refused, what: "refused");

            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("hel")));
            var surface = h.Surfaces.Single();
            await Assert.That(surface.Fed).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    /// The server holds a viewer's size until told otherwise, so a detach gives it back at once: a
    /// release that waited for its unsubscribe to return would land after the next attach's report
    /// and take that one back instead.
    [Test]
    public async Task A_detach_gives_its_viewport_back_before_the_next_attach_reports() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var unsubscribe = new TaskCompletionSource<HubCallOutcome>();
            h.Lane.TerminalUnsubscribeHandler = _ => unsubscribe.Task;
            await WaitUntilAsync(() => h.Lane.Resizes.Count == 1, what: "the first viewport");

            h.Drop();
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Count == 1, what: "the viewport released");
            h.Reconnect();
            await WaitUntilAsync(() => h.Lane.Resizes.Count == 2, what: "the second viewport");
            var calls = h.Lane.Calls.ToList();
            await Assert.That(calls.IndexOf("release:a1")).IsLessThan(calls.LastIndexOf("resize:a1:80x24"));

            unsubscribe.SetResult(HubCallOutcome.Ok);
            await Task.Delay(50);
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(1);

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(2);
        });
    }

    /// The subscription is the agent's, not the session's, so a terminal already live when session
    /// access is established — the session starting while it is watched — carries on as it is. A
    /// later establishment can come with a replacement terminal, so that one asks again.
    [Test]
    public async Task A_live_terminal_carries_on_when_the_session_starts_and_asks_again_after() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Unavailable);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var surface = h.Vm.Surface;

            h.Access.OnNext(SessionAccessState.Establishing);
            h.Access.OnNext(SessionAccessState.Established);
            await Task.Delay(50);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Live);
            await Assert.That(h.Vm.Surface).IsSameReferenceAs(surface);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();

            h.Access.OnNext(SessionAccessState.Establishing);
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live again");
            await Assert.That(h.Vm.Surface).IsNotSameReferenceAs(surface);
            await h.Vm.TeardownAsync();
        });
    }

    /// An ask in flight when access is established may have been answered before it: the
    /// establishment asks again rather than leaving that answer to stand.
    [Test]
    public async Task Access_established_while_an_ask_is_in_flight_asks_again() {
        await RunOnUiAsync(async () => {
            var first = new TaskCompletionSource<HubCallOutcome>();
            var calls = 0;
            var h = new Harness(subscribe: _ => calls++ == 0 ? first.Task : Task.FromResult(HubCallOutcome.Ok));
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 1, what: "the first subscribe");

            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            first.SetResult(HubCallOutcome.Failed("retry"));
            await Task.Delay(50);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Live);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    /// Before a session no access verdict arrives to ask again, so a failed ask backs off and retries.
    [Test]
    public async Task A_failed_ask_retries_after_a_back_off() {
        await RunOnUiAsync(async () => {
            var fail = true;
            var h = new Harness(subscribe: _ => Task.FromResult(fail ? HubCallOutcome.Failed("retry") : HubCallOutcome.Ok));
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");

            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);

            fail = false;
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    /// A refusal is the server's answer and stands until it could change: established access, as
    /// when the session starts and the viewer holds Full on it, asks again; a denial does not.
    [Test]
    public async Task A_refusal_stands_until_access_is_established() {
        await RunOnUiAsync(async () => {
            var refuse = true;
            var h = new Harness(subscribe: _ => Task.FromResult(refuse ? HubCallOutcome.Denied("not admitted") : HubCallOutcome.Ok));
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Refused, what: "refused");
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsFalse();

            h.Access.OnNext(SessionAccessState.Unavailable);
            h.Access.OnNext(SessionAccessState.Establishing);
            h.Access.OnNext(SessionAccessState.Denied);
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);

            refuse = false;
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    /// The server ends a revoked viewer's stream without a word, so a denial of session access asks
    /// again, giving back the live subscription and its viewport first.
    [Test]
    public async Task A_denial_of_session_access_asks_again() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Lane.Resizes.Count == 1, what: "the viewport");

            h.Lane.TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.Denied("not admitted"));
            h.Access.OnNext(SessionAccessState.Denied);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Refused, what: "refused");
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEquivalentTo(new[] { "a1" });
            await Assert.That(h.Lane.ResizeReleases).IsEquivalentTo(new[] { "a1" });
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    /// A transient registry snapshot drops the row and the next refresh restores the same live
    /// session; the host withdraws its ended verdict then, and the terminal follows it back.
    [Test]
    public async Task A_session_that_returns_after_reading_as_ended_attaches_again() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");

            h.Ended.OnNext(true);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Ended);
            await WaitUntilAsync(() => h.Lane.TerminalUnsubscribes.Count == 1, what: "the unsubscribe");

            h.Ended.OnNext(false);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live again");
            await Assert.That(h.Surfaces.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }
}
