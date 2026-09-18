namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.ProbeVendorsConcurrently{T}"/> is the seam that keeps startup from
/// serializing one bounded vendor `--version` probe behind another: N vendors probed concurrently
/// cost roughly one probe's budget, not the sum across N. These pin the two properties startup relies
/// on — the probes overlap, and a probe still unfinished at the ceiling maps to the miss value without
/// holding the rest of the pass hostage.
/// </summary>
public class DaemonRunnerConcurrentProbeTests {
    /// <summary>Every probe waits for all of its peers to be in flight before it returns, so a pass that
    /// ran them one at a time could never complete even one. The thread pool is saturated first: the
    /// overlap has to hold when the daemon's pool is busy, and a probe queued on it would start only as
    /// the pool grew. The pool is process-wide, hence the bare exclusion. Both waits are event waits,
    /// like the real probe's WaitForExit: a Task.Wait on a pool thread tells the pool it is blocked and
    /// gets a compensating thread, which is exactly what the probes do not get.</summary>
    [Test]
    [NotInParallel]
    public async Task ProbeVendorsConcurrently_OverlapsProbes_EvenWhenThreadPoolIsSaturated() {
        string[] vendors = ["a", "b", "c", "d", "e"];
        using var hold  = new ManualResetEventSlim(false);
        // Not disposed: a probe stranded by a regression can still be inside Wait when the pass returns.
        var allInFlight = new CountdownEvent(vendors.Length);

        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var blockers = Enumerable.Range(0, Math.Max(minWorkers, ThreadPool.ThreadCount) + 4)
            .Select(_ => Task.Run(() => hold.Wait()))
            .ToArray();

        IReadOnlyDictionary<string, string> result;
        try {
            result = DaemonRunner.ProbeVendorsConcurrently(
                vendors,
                vendor => {
                    allInFlight.Signal();

                    return allInFlight.Wait(2_000) ? vendor + "-ok" : vendor + "-alone";
                },
                timedOut: "TIMEOUT",
                ceilingMs: 2_000);
        }
        finally { hold.Set(); }
        await Task.WhenAll(blockers);

        await Assert.That(result.Keys).IsEquivalentTo(vendors);
        await Assert.That(result.Values).IsEquivalentTo(vendors.Select(v => v + "-ok").ToArray());
    }

    /// <summary>A single slow vendor is cut at the ceiling and maps to the miss value; the vendors that
    /// answered still resolve, so one wedged CLI does not withhold the rest of the advertisement.</summary>
    [Test]
    public async Task ProbeVendorsConcurrently_SlowVendorMapsToTimeout_FastVendorsStillResolve() {
        string[] vendors = ["fast1", "slow", "fast2"];
        var wedged = new TaskCompletionSource();

        IReadOnlyDictionary<string, string> result;
        try {
            result = DaemonRunner.ProbeVendorsConcurrently(
                vendors,
                vendor => {
                    if (vendor == "slow") wedged.Task.Wait();

                    return vendor + "-v";
                },
                timedOut: "TIMEOUT",
                ceilingMs: 800);
        }
        finally { wedged.SetResult(); }

        await Assert.That(result["fast1"]).IsEqualTo("fast1-v");
        await Assert.That(result["fast2"]).IsEqualTo("fast2-v");
        await Assert.That(result["slow"]).IsEqualTo("TIMEOUT");
    }

    [Test]
    public async Task ProbeVendorsConcurrently_NoVendors_ReturnsEmpty() {
        var result = DaemonRunner.ProbeVendorsConcurrently(
            Array.Empty<string>(), _ => "unused", timedOut: "TIMEOUT");

        await Assert.That(result).IsEmpty();
    }
}
