using Avalonia.Threading;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// Every read settles through Dispatcher.UIThread, so every test runs under RunOnUiAsync.
[NotInParallel("AvaloniaSession")]
public class SingleFlightReaderTests {
    /// An apply that switches the reader away releases the lease it is settling, so a refresh
    /// queued on that lease must not restart it: teardown would then cancel a disposed source.
    [Test]
    public async Task A_lease_released_while_its_result_applies_is_not_restarted() {
        await RunOnUiAsync(async () => {
            var reads = new List<(string State, TaskCompletionSource<string> Result)>();
            var errors = new List<Exception>();
            var teardown = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            SingleFlightReader<string, string>? reader = null;
            reader = new SingleFlightReader<string, string>(
                (state, ct) => {
                    var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(() => result.TrySetCanceled(ct));
                    reads.Add((state, result));
                    return result.Task;
                },
                ex => { errors.Add(ex); return "error"; },
                result => {
                    if (result != "a") return;
                    reader!.Switch("b");
                    // Queued ahead of any settle a restarted lease would post.
                    Dispatcher.UIThread.Post(() => teardown.SetResult(reader.TeardownAsync()));
                },
                "test");

            reader.Switch("a");
            reader.Refresh();
            reads[0].Result.SetResult("a");
            var teardownTask = await teardown.Task;

            await Assert.That(async () => await teardownTask).ThrowsNothing();
            await Assert.That(errors).IsEmpty();
            await Assert.That(reads.Select(r => r.State)).IsEquivalentTo(["a", "b"]);
        });
    }

    [Test]
    public async Task A_cached_result_applies_settled_at_the_switch_and_the_read_still_runs() {
        await RunOnUiAsync(async () => {
            var applied = new List<string>();
            var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = new SingleFlightReader<string, string>(
                (_, _) => read.Task, _ => "error", applied.Add, "test",
                cached: state => state == "a" ? "cached a" : null);

            reader.Switch("a");
            await Assert.That(applied).IsEquivalentTo(["cached a"]);
            await Assert.That(reader.IsCurrentSettled).IsTrue();

            read.SetResult("fresh a");
            await (reader.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(applied).IsEquivalentTo(["cached a", "fresh a"]);
            await reader.TeardownAsync();
        });
    }
}
