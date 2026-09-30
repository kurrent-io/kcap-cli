namespace Capacitor.App.Tests.Unit;

public class ScriptedLocalControlOpsTests {
    /// Concurrent callers each take a distinct armed gate and every payload is recorded — the
    /// property AgentActionServiceTests' concurrent-stop test relies on.
    [Test]
    public async Task Concurrent_stops_each_take_a_distinct_gate_and_are_all_recorded() {
        const int n = 16;
        var ops = new ScriptedLocalControlOps();
        var gates = Enumerable.Range(0, n).Select(_ => ops.ArmStop()).ToArray();
        using var start = new Barrier(n);

        var calls = Enumerable.Range(0, n)
            .Select(i => Task.Factory.StartNew(() => {
                start.SignalAndWait();
                return ops.StopAgentAsync($"a{i}", false, CancellationToken.None);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        var returned = await Task.WhenAll(calls);

        var gateTasks = gates.Select(g => g.Task).ToHashSet();
        await Assert.That(returned.Distinct().Count()).IsEqualTo(n);
        await Assert.That(returned.All(gateTasks.Contains)).IsTrue();
        await Assert.That(ops.StopCalls).IsEqualTo(n);
        await Assert.That(ops.StopPayloads.Select(p => p.AgentId).Order())
            .IsEquivalentTo(Enumerable.Range(0, n).Select(i => $"a{i}").Order());
    }
}
