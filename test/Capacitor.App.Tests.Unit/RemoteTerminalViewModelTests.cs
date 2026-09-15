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
/// reported while live and released on close, and a fresh surface per subscription.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteTerminalViewModelTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<bool> Ended = new(false);
        public readonly List<FakeTerminalSurface> Surfaces = [];
        public readonly RemoteTerminalViewModel Vm;

        public Harness() {
            Vm = new RemoteTerminalViewModel("a1", Lane, Access, Ended, () => { var s = new FakeTerminalSurface(); Surfaces.Add(s); return s; });
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
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the release");
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
}
