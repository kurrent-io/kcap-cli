namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// <see cref="DaemonRunner.ProbeVendorsConcurrently{T}"/> is the seam that keeps startup from
/// serializing one bounded vendor `--version` probe behind another: N vendors probed concurrently
/// cost roughly one probe's budget, not the sum across N. These pin the two properties startup relies
/// on — the probes overlap, and a probe still unfinished at the ceiling maps to the miss value without
/// holding the rest of the pass hostage.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class DaemonRunnerConcurrentProbeTests {
    static void InterlockedMax(ref int target, int value) {
        int seen;
        do { seen = Volatile.Read(ref target); }
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    /// <summary>A sequential pass can never hold two probes in flight at once, so an observed peak of
    /// two or more is proof the pass overlapped them.</summary>
    [Test]
    public async Task ProbeVendorsConcurrently_OverlapsProbes_RatherThanSerializing() {
        string[] vendors = ["a", "b", "c", "d", "e"];
        var current       = 0;
        var maxConcurrent = 0;

        var result = DaemonRunner.ProbeVendorsConcurrently(
            vendors,
            vendor => {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref maxConcurrent, now);
                Thread.Sleep(300);   // hold the slot so peers can pile up
                Interlocked.Decrement(ref current);

                return vendor + "-ok";
            },
            timedOut: "TIMEOUT");

        await Assert.That(maxConcurrent).IsGreaterThanOrEqualTo(2)
            .Because("a sequential pass would never show two probes in flight at once");
        await Assert.That(result.Keys).IsEquivalentTo(vendors);
        await Assert.That(result.Values.All(v => v == "TIMEOUT")).IsFalse();
        await Assert.That(result["c"]).IsEqualTo("c-ok");
    }

    /// <summary>A single slow vendor is cut at the ceiling and maps to the miss value; the vendors that
    /// answered still resolve, so one wedged CLI does not withhold the rest of the advertisement.</summary>
    [Test]
    public async Task ProbeVendorsConcurrently_SlowVendorMapsToTimeout_FastVendorsStillResolve() {
        string[] vendors = ["fast1", "slow", "fast2"];

        var result = DaemonRunner.ProbeVendorsConcurrently(
            vendors,
            vendor => {
                if (vendor == "slow") Thread.Sleep(4_000);

                return vendor + "-v";
            },
            timedOut: "TIMEOUT",
            ceilingMs: 800);

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
