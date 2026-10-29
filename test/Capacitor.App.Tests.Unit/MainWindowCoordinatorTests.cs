using Capacitor.App.Services;

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
}
