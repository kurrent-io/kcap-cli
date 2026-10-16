namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Console is process-global, so the capture cannot overlap any other capture in the assembly.
/// </summary>
[NotInParallel]
public class ConsoleOutputTests {
    [Test]
    public async Task A_snapshot_during_a_concurrent_write_does_not_throw() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() => {
            while (!stop.IsCancellationRequested)
                Console.Error.WriteLine("boom");
        });

        try {
            var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
            while (DateTime.UtcNow < until)
                _ = capture.GetCapturedError();
        }
        finally {
            stop.Cancel();
            await writer;
        }

        await Assert.That(capture.GetCapturedError()).Contains("boom");
    }
}
