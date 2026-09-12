using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
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
