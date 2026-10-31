using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class MainWindowCoordinatorTests {
    /// Automatic handover must not reveal the new main window while the predecessor is still quitting.
    [Test]
    public async Task Automatic_update_handover_waits_for_earlier_instances_to_finish() {
        var earlier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = false;
        var completion = App.CompleteUpdateAsync(earlier.Task, () => opened = true);

        await Assert.That(opened).IsFalse();
        earlier.SetResult();
        await completion;
        await Assert.That(opened).IsTrue();
    }

    /// A held launch never builds the main window: the factory would throw here.
    [Test]
    public async Task A_held_launch_raises_its_own_window_for_every_request_to_show_the_main_one() {
        var raised = 0;
        var coordinator = new MainWindowCoordinator(() => throw new InvalidOperationException("built the main window")) {
            ShowInstead = () => raised++,
        };

        coordinator.ShowMainWindow();
        App.MainWindowAction(coordinator)?.Invoke();

        await Assert.That(raised).IsEqualTo(2);
        await Assert.That(coordinator.Window).IsNull();
    }

    /// Each trip loses the maximized state while the window is hidden, as Win32 does for a maximize
    /// the OS performed and an X11 window manager does on unmap; each must still come back maximized.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_maximized_window_comes_back_maximized_from_every_trip_to_the_tray() {
        var states = await AvaloniaSession.DispatchAsync(() => {
            var coordinator = Shown();
            var window = coordinator.Window!;
            window.WindowState = WindowState.Maximized;
            var seen = new List<WindowState>();
            for (var trip = 0; trip < 3; trip++) {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                window.WindowState = WindowState.Normal;
                coordinator.ShowMainWindow();
                Dispatcher.UIThread.RunJobs();
                seen.Add(window.WindowState);
            }
            Quit(coordinator);
            return seen;
        });

        await Assert.That(states).IsEquivalentTo(
            [WindowState.Maximized, WindowState.Maximized, WindowState.Maximized], CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_window_closed_to_the_tray_while_minimized_comes_back_as_it_was_before() {
        var state = await AvaloniaSession.DispatchAsync(() => {
            var coordinator = Shown();
            var window = coordinator.Window!;
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();
            var result = window.WindowState;
            Quit(coordinator);
            return result;
        });

        await Assert.That(state).IsEqualTo(WindowState.Maximized);
    }

    /// The headless screen is 1920x1280: a frame left past its edge is pulled fully back onto it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_normal_window_off_the_screen_comes_back_inside_its_work_area() {
        var (position, width, state) = await AvaloniaSession.DispatchAsync(() => {
            var coordinator = Shown();
            var window = coordinator.Window!;
            window.Width = 1400;
            window.Height = 760;
            window.Position = new PixelPoint(5000, 5000);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();
            var result = (window.Position, window.Width, window.WindowState);
            Quit(coordinator);
            return result;
        });

        await Assert.That(position).IsEqualTo(new PixelPoint(520, 520));
        await Assert.That(width).IsEqualTo(1400d);
        await Assert.That(state).IsEqualTo(WindowState.Normal);
    }

    /// A notification opening a session navigates before the window reappears, so what the hide
    /// parked is not rebuilt first only to be replaced.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Navigating_while_showing_runs_before_the_parked_workspace_is_restored() {
        var order = await AvaloniaSession.DispatchAsync(() => {
            var calls = new List<string>();
            var coordinator = new MainWindowCoordinator(
                () => {
                    var window = new MainWindow();
                    window.Show();
                    return window;
                },
                parkWorkspace: _ => calls.Add("park"),
                restoreWorkspace: _ => calls.Add("restore"));
            coordinator.ShowMainWindow();
            coordinator.Window!.Close();
            Dispatcher.UIThread.RunJobs();
            coordinator.ShowMainWindow(window => calls.Add(window.IsVisible ? "navigate shown" : "navigate hidden"));
            Dispatcher.UIThread.RunJobs();
            Quit(coordinator);
            return calls;
        });

        await Assert.That(order).IsEquivalentTo(["park", "navigate hidden", "restore"], CollectionOrdering.Matching);
    }

    static MainWindowCoordinator Shown() {
        var coordinator = new MainWindowCoordinator(() => {
            var window = new MainWindow();
            window.Show();
            return window;
        });
        coordinator.ShowMainWindow();
        return coordinator;
    }

    static void Quit(MainWindowCoordinator coordinator) {
        coordinator.QuitInProgress = true;
        coordinator.Window?.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
