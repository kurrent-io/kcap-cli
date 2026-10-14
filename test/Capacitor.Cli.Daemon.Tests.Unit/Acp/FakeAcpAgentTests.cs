using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

public class FakeAcpAgentTests {
    [Test]
    public async Task Dispatch_fault_is_exposed_and_rethrown_on_disposal() {
        var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();
        var run = fake.RunAsync(cts.Token);
        try {
            await fake.ClientWriteStream.WriteAsync(Encoding.UTF8.GetBytes("not valid json\n"), cts.Token);
            await fake.ClientWriteStream.FlushAsync(cts.Token);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fake.DispatchFault is null)
                await Task.Delay(10, guard.Token);

            await Assert.That(fake.DispatchFault).IsTypeOf<JsonException>();
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        } catch {
            cts.Cancel();
            // Cleanup must preserve the body failure, including a fault-capture timeout.
            try { await run; } catch (Exception) { }
            try { await fake.DisposeAsync(); } catch (Exception) { }
            throw;
        }

        await Assert.ThrowsAsync<JsonException>(async () => await fake.DisposeAsync());
    }
}
