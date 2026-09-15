using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The read-only terminal: subscribed only once access stands, sized by the source, its viewport
/// reported while the pane is showing and released when it stops driving, and a fresh surface per
/// subscription.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteTerminalViewModelTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<bool> Ended = new(false);
        public readonly BehaviorSubject<bool> Visible;
        public readonly List<FakeTerminalSurface> Surfaces = [];
        public readonly RemoteTerminalViewModel Vm;

        public Harness(bool visible = true) {
            Visible = new BehaviorSubject<bool>(visible);
            Vm = new RemoteTerminalViewModel("a1", Lane, Access, Ended, Visible, () => { var s = new FakeTerminalSurface(); Surfaces.Add(s); return s; });
        }

        public static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    [Test]
    public async Task Subscribes_only_once_access_is_established_then_shows_the_replay_and_live_frames() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Waiting);
            await Assert.That(h.Lane.TerminalSubscribes).IsEmpty();

            h.Access.OnNext(SessionAccessState.Established);
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
            h.Access.OnNext(SessionAccessState.Established);
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
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            h.Surfaces.Single().RaiseResize(100, 30);
            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 100, 30)), what: "the viewport");

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.TerminalUnsubscribes).Contains("a1");
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");
            await Assert.That(h.Vm.Surface).IsNull();
        });
    }

    [Test]
    public async Task A_re_established_access_subscribes_again_onto_a_fresh_surface() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var first = h.Vm.Surface;

            h.Access.OnNext(SessionAccessState.Unavailable);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Offline);
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the viewport released");

            h.Access.OnNext(SessionAccessState.Establishing);
            h.Access.OnNext(SessionAccessState.Established);
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
            var h = new Harness();
            var subscribeSource = new TaskCompletionSource<HubCallOutcome>();
            h.Lane.TerminalSubscribeHandler = _ => subscribeSource.Task;
            h.Access.OnNext(SessionAccessState.Established);
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
    public async Task A_refused_subscribe_reads_as_offline_and_an_ended_session_as_ended() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Lane.TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            h.Access.OnNext(SessionAccessState.Established);
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
            var h = new Harness();
            var subscribeSource = new TaskCompletionSource<HubCallOutcome>();
            h.Lane.TerminalSubscribeHandler = _ => subscribeSource.Task;
            h.Access.OnNext(SessionAccessState.Established);
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

    /// The server takes the smallest viewport across viewers, so a pane nobody is looking at —
    /// still carrying its unmeasured constructor size — would clamp the live agent's PTY for
    /// everyone. The subscription itself stays eager: the replay is what the tab switch shows.
    [Test]
    public async Task A_hidden_pane_subscribes_without_a_viewport_and_reports_one_when_it_is_shown() {
        await RunOnUiAsync(async () => {
            var h = new Harness(visible: false);
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes).Contains("a1");
            await Assert.That(h.Lane.Resizes).IsEmpty();

            h.Visible.OnNext(true);
            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 80, 24)), what: "the viewport");

            h.Visible.OnNext(false);
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the viewport released");
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(1);
        });
    }

    /// The hub's bounds are 1..500 × 1..200, and (0,0) is its own clear sentinel.
    [Test]
    public async Task A_size_outside_the_servers_bounds_is_never_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var reported = h.Lane.Resizes.Count;
            var surface = h.Surfaces.Single();

            surface.RaiseResize(0, 0);
            surface.RaiseResize(501, 30);
            surface.RaiseResize(100, 201);
            surface.RaiseResize(100, 30);

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
            var h = new Harness();
            var stale = new TaskCompletionSource<HubCallOutcome>();
            var calls = 0;
            h.Lane.TerminalSubscribeHandler = _ => ++calls == 1 ? stale.Task : Task.FromResult(HubCallOutcome.Ok);
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 1, what: "the first subscribe");

            h.Access.OnNext(SessionAccessState.Unavailable);
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");

            stale.SetResult(HubCallOutcome.Ok);
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalUnsubscribes).IsEmpty();
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(2);
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

            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsTrue();

            h.Access.OnNext(SessionAccessState.Unavailable);
            await Assert.That(await h.Vm.SendKeyCommand.CanExecute.FirstAsync()).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_refused_subscribe_drops_later_frames() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Lane.TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.Denied("x"));
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");

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
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Lane.Resizes.Count == 1, what: "the first viewport");

            h.Access.OnNext(SessionAccessState.Unavailable);
            await Assert.That(h.Lane.ResizeReleases.Count).IsEqualTo(1);
            h.Access.OnNext(SessionAccessState.Established);
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

    /// A transient registry snapshot drops the row and the next refresh restores the same live
    /// session; the host withdraws its ended verdict then, and the terminal follows it back.
    [Test]
    public async Task A_session_that_returns_after_reading_as_ended_attaches_again() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
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
