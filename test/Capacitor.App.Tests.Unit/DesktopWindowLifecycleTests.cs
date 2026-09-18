using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class DesktopWindowLifecycleTests {
    [Test]
    public Task Dock_reopen_after_close_restores_the_same_window_and_releases_its_workspace_once() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            var window = fixture.Coordinator.Window!;
            window.Close();
            await Assert.That(window.IsVisible).IsFalse();

            fixture.Activation.Raise(ActivationKind.Reopen);
            fixture.Activation.Raise(ActivationKind.Reopen);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(window.IsVisible).IsTrue();
            await Assert.That(fixture.Coordinator.Window).IsSameReferenceAs(window);
            await Assert.That(fixture.Builds).IsEqualTo(1);
            await Assert.That(fixture.Releases).IsEqualTo(1);
            await Assert.That(fixture.DockVisibility).IsEquivalentTo([true, false, true], CollectionOrdering.Matching);
        });

    [Test]
    public Task Settings_keeps_the_Dock_visible_until_the_last_window_closes() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            var settings = new SettingsWindow();
            try {
                settings.Show();
                fixture.Coordinator.Window!.Close();
                await Assert.That(fixture.DockVisibility).IsEquivalentTo([true], CollectionOrdering.Matching);

                settings.Close();
                await Assert.That(fixture.DockVisibility).IsEquivalentTo([true, false], CollectionOrdering.Matching);

                fixture.Coordinator.ShowMainWindow();
                await Assert.That(fixture.DockVisibility).IsEquivalentTo([true, false, true], CollectionOrdering.Matching);
            } finally {
                settings.Close();
            }
        });

    [Test]
    public Task Minimize_keeps_the_Dock_visible_and_reopen_restores_the_window() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            var window = fixture.Coordinator.Window!;
            window.WindowState = WindowState.Minimized;
            await Assert.That(fixture.DockVisibility).IsEquivalentTo([true], CollectionOrdering.Matching);

            fixture.Activation.Raise(ActivationKind.Reopen);

            await Assert.That(window.WindowState).IsEqualTo(WindowState.Normal);
            await Assert.That(fixture.DockVisibility).IsEquivalentTo([true], CollectionOrdering.Matching);
        });

    [Test]
    [Arguments(WindowState.Maximized)]
    [Arguments(WindowState.FullScreen)]
    public Task Reopen_preserves_an_expanded_window(WindowState state) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            fixture.Coordinator.Window!.WindowState = state;

            fixture.Activation.Raise(ActivationKind.Reopen);

            await Assert.That(fixture.Coordinator.Window.WindowState).IsEqualTo(state);
        });

    [Test]
    [Arguments(ActivationKind.Background)]
    [Arguments(ActivationKind.File)]
    [Arguments(ActivationKind.OpenUri)]
    public Task Other_activation_kinds_do_not_reopen_a_hidden_window(ActivationKind kind) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            fixture.Coordinator.Window!.Close();

            fixture.Activation.Raise(kind);

            await Assert.That(fixture.Coordinator.Window.IsVisible).IsFalse();
        });

    [Test]
    public Task Reopen_resolves_the_action_after_startup_finishes() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture { Ready = false };
            fixture.Activation.Raise(ActivationKind.Reopen);
            await Assert.That(fixture.Builds).IsEqualTo(0);

            fixture.Ready = true;
            fixture.Activation.Raise(ActivationKind.Reopen);

            await Assert.That(fixture.Builds).IsEqualTo(1);
            await Assert.That(fixture.Coordinator.Window!.IsVisible).IsTrue();
        });

    [Test]
    public Task Reopen_and_tray_open_during_quit_do_not_show_or_rebuild_the_window() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            var window = fixture.Coordinator.Window!;
            window.Close();
            fixture.Coordinator.QuitInProgress = true;

            fixture.Activation.Raise(ActivationKind.Reopen);
            fixture.Coordinator.ShowMainWindow();
            await Assert.That(window.IsVisible).IsFalse();

            window.Close();
            fixture.Activation.Raise(ActivationKind.Reopen);
            fixture.Coordinator.ShowMainWindow();
            await Assert.That(fixture.Coordinator.Window).IsNull();
            await Assert.That(fixture.Builds).IsEqualTo(1);
        });

    [Test]
    public Task Disposal_detaches_activation_and_window_tracking() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var fixture = new Fixture();
            fixture.Coordinator.ShowMainWindow();
            fixture.Lifecycle.Dispose();
            fixture.Lifecycle.Dispose();
            fixture.Coordinator.Window!.Close();

            fixture.Activation.Raise(ActivationKind.Reopen);
            var other = new Window();
            other.Show();
            other.Close();

            await Assert.That(fixture.Coordinator.Window.IsVisible).IsFalse();
            await Assert.That(fixture.DockVisibility).IsEquivalentTo([true], CollectionOrdering.Matching);
        });

    [Test]
    public Task A_platform_without_activation_support_still_tracks_window_visibility() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var visibility = new List<bool>();
            using var lifecycle = new DesktopWindowLifecycle(null, () => null, visibility.Add);
            var window = new Window();
            window.Show();
            window.Hide();
            window.Show();
            window.Close();

            await Assert.That(visibility).IsEquivalentTo([true, false, true, false], CollectionOrdering.Matching);
        });

    [Test]
    public Task Feedback_window_is_single_instance_and_switches_category() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var app = new Capacitor.App.App();
            app.OpenFeedback(new SilentFeedbackApi(), Trailer, null, FeedbackCategory.Bug);
            var first = app.FeedbackWindowForTests!;
            try {
                app.OpenFeedback(new SilentFeedbackApi(), Trailer, null, FeedbackCategory.Feedback);

                await Assert.That(app.FeedbackWindowForTests).IsSameReferenceAs(first);
                await Assert.That(((FeedbackViewModel)first.DataContext!).Category).IsEqualTo(FeedbackCategory.Feedback);
            } finally {
                first.Close();
            }

            await Assert.That(app.FeedbackWindowForTests).IsNull();
        });

    [Test]
    public Task Shutdown_closes_an_idle_feedback_window() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var app = new Capacitor.App.App();
            app.OpenFeedback(new SilentFeedbackApi(), Trailer, null, FeedbackCategory.Bug);
            // Without this the assertion below would pass on a window that never opened.
            await Assert.That(app.FeedbackWindowForTests).IsNotNull();

            await app.DisposeAndShutdownAsync();

            await Assert.That(app.FeedbackWindowForTests).IsNull();
        });

    [Test]
    public Task Feedback_window_refuses_to_close_while_a_send_is_in_flight() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var app = new Capacitor.App.App();
            var api = new BlockingFeedbackApi();
            app.OpenFeedback(api, Trailer, null, FeedbackCategory.Bug);
            var window = app.FeedbackWindowForTests!;
            var vm = (FeedbackViewModel)window.DataContext!;
            vm.Message = "It broke.";

            var send = vm.SendCommand.Execute().ToTask();
            try {
                await api.Started.Task;

                window.Close();

                await Assert.That(app.FeedbackWindowForTests).IsSameReferenceAs(window);
                await Assert.That(window.IsVisible).IsTrue();
            } finally {
                api.Release(new FeedbackResult.Sent("a@b.c"));
                await send;
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    /// A quit must close a busy feedback window: left open it cancels its own close, the shutdown
    /// aborts with windows up, and every later quit early-returns — an app only force-quit ends.
    [Test]
    public Task Shutdown_closes_a_feedback_window_that_is_mid_send() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var app = new Capacitor.App.App();
            var api = new BlockingFeedbackApi();
            app.OpenFeedback(api, Trailer, null, FeedbackCategory.Bug);
            var window = app.FeedbackWindowForTests!;
            var vm = (FeedbackViewModel)window.DataContext!;
            vm.Message = "It broke.";

            var send = vm.SendCommand.Execute().ToTask();
            await api.Started.Task;
            // Otherwise the close below would prove nothing: an idle window closes either way.
            await Assert.That(vm.IsBusy).IsTrue();

            await app.StartShutdownAsync();

            await Assert.That(app.FeedbackWindowForTests).IsNull();
            await Assert.That(window.IsVisible).IsFalse();

            api.Release(new FeedbackResult.Sent("a@b.c"));
            await send;
        });

    /// The trailer carries the daemon version the status line shows, so a report and the version
    /// chip in the window it was sent from can never disagree. Snapshots reach the feed on the
    /// daemon client's pump thread, and the trailer drives a bound hint, so the feed marshals.
    [Test]
    public Task Feedback_trailer_strips_the_daemon_build_metadata_and_lands_on_the_ui_thread() =>
        AvaloniaSession.DispatchAsync(async () => {
            var service = new FakeDaemonClientService { DaemonName = "daemon-a" };
            var ui      = Environment.CurrentManagedThreadId;
            var emitted = new List<string>();
            var threads = new List<int>();

            using var feed = Capacitor.App.App.FeedbackTrailerFeed(service, "9.9.9", () => "0.0.1")
                .Subscribe(t => { emitted.Add(t); threads.Add(Environment.CurrentManagedThreadId); });
            // The seed is synchronous, so a window never opens on an empty trailer.
            await Assert.That(emitted).Count().IsEqualTo(1);

            var pump = new Thread(() => service.SnapshotsSubject.OnNext(
                FakeDaemonClientService.Snap(daemon: "daemon-a", version: "1.0.3+abc123")));
            pump.Start();
            pump.Join();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(emitted).Count().IsEqualTo(2);
            await Assert.That(threads[^1]).IsEqualTo(ui);
            await Assert.That(emitted[^1]).IsEqualTo("Sent from Kurrent Capacitor Desktop 9.9.9 · daemon daemon-a 1.0.3");
            await Assert.That(emitted[^1]).DoesNotContain("abc123");
            return true;
        });

    static IObservable<string> Trailer =>
        Observable.Return("Sent from Kurrent Capacitor Desktop 1.0.3 · daemon d 1.0.3");

    sealed class SilentFeedbackApi : IFeedbackApi {
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) =>
            throw new NotSupportedException("these tests never send");
    }

    sealed class BlockingFeedbackApi : IFeedbackApi {
        readonly TaskCompletionSource<FeedbackResult> _gate = new();
        public TaskCompletionSource Started { get; } = new();
        public void Release(FeedbackResult result) => _gate.TrySetResult(result);
        public async Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) {
            Started.TrySetResult();
            return await _gate.Task;
        }
    }

    sealed class Fixture : IDisposable {
        public FakeActivatableLifetime Activation { get; } = FakeActivatableLifetime.Create();
        public List<bool> DockVisibility { get; } = [];
        public MainWindowCoordinator Coordinator { get; }
        public DesktopWindowLifecycle Lifecycle { get; }
        public bool Ready { get; set; } = true;
        public int Builds { get; private set; }
        public int Releases { get; private set; }

        public Fixture() {
            Coordinator = new MainWindowCoordinator(() => {
                Builds++;
                var window = new MainWindow();
                window.Show();
                return window;
            }, _ => Releases++);
            Lifecycle = new DesktopWindowLifecycle(Activation.Lifetime,
                () => Capacitor.App.App.MainWindowAction(Ready ? Coordinator : null), DockVisibility.Add);
        }

        public void Dispose() {
            Lifecycle.Dispose();
            Coordinator.QuitInProgress = true;
            Coordinator.Window?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
