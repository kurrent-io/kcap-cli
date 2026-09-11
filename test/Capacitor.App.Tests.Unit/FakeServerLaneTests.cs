namespace Capacitor.App.Tests.Unit;

/// The lane's call logs are written from pool threads (every service under test dispatches its hub
/// calls off the caller's thread) and enumerated from the test thread, so they have to survive
/// both at once: no "Collection was modified", and no dropped entry.
public class FakeServerLaneTests {
    [Test]
    public async Task Call_logs_survive_concurrent_writers_and_a_reader() {
        const int writers = 8, calls = 200;
        var lane = new FakeServerLane();
        using var start = new Barrier(writers + 1);
        var reading = true;

        var reader = Task.Run(() => {
            while (Volatile.Read(ref reading)) {
                _ = lane.Calls.Count;
                _ = lane.ChatSubscribes.Count(s => s == "s1");
                _ = lane.Stops.Contains("a1");
            }
        });
        var writerTasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () => {
            start.SignalAndWait();
            for (var i = 0; i < calls; i++) {
                await lane.SubscribeToChatAsync("s1", CancellationToken.None);
                await lane.RequestStopAgentAsync("a1", CancellationToken.None);
            }
        })).ToArray();

        start.SignalAndWait();
        await Task.WhenAll(writerTasks);
        Volatile.Write(ref reading, false);
        await reader;

        await Assert.That(lane.ChatSubscribes.Count).IsEqualTo(writers * calls);
        await Assert.That(lane.Stops.Count).IsEqualTo(writers * calls);
        await Assert.That(lane.Calls.Count).IsEqualTo(writers * calls * 2);
    }
}
