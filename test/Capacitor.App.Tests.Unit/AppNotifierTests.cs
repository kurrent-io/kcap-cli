using Capacitor.App.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Notify is called from concurrent Task.Run bodies (AgentActionService's per-agent stops, pause
/// ops), and Rx requires OnNext on one Subject to be serialized, so AppNotifier.Notify holds one
/// lock around both the Subject push and the Console.Error write. The race itself is
/// non-deterministic and not asserted here; this pins that sequential calls deliver in the same
/// relative order to both channels.
public class AppNotifierTests {
    // Swaps the process-global Console.Error — bare NotInParallel (not just a group key) is
    // required, same reasoning as ImportVisibilityTests' Console-redirecting tests: a group key
    // alone would not stop a DIFFERENT group's Console-redirecting test from racing on the same
    // process-global state.
    [Test, NotInParallel]
    public async Task Two_sequential_notifies_deliver_in_order_to_both_channels() {
        var notifier = new AppNotifier();
        var received = new List<string>();
        using var subscription = notifier.Messages.Subscribe(received.Add);

        using var capture = ConsoleOutput.StartErrorCapture();
        notifier.Notify("first");
        notifier.Notify("second");

        await Assert.That(received).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);

        var stderrLines = capture.GetCapturedError().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(stderrLines).IsEquivalentTo(["kcap: first", "kcap: second"], CollectionOrdering.Matching);
    }
}
