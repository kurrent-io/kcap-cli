using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class MainWindowCoordinatorTests {
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
