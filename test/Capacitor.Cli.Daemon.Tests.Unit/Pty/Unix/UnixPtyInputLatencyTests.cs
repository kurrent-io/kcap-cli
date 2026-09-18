using System.Globalization;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// Pins that draining idle PTYs costs the thread pool nothing. The probe runs in a process of its
/// own, with more idle PTYs than the pool's worker minimum and that minimum left alone — raising
/// it would hide readers parked on workers rather than prove there are none.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class UnixPtyInputLatencyTests {
    // A reader parked on a worker costs a pool injection, about 500 ms each, and the probe queues
    // eight of them ahead of the write.
    static readonly TimeSpan EchoBudget   = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    [Test]
    public async Task Keystrokes_echo_promptly_while_more_idle_ptys_than_pool_workers_are_drained() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var probe = await RunProbeAsync();

        await Assert.That(probe.LatenciesMs.Max()).IsLessThan(EchoBudget.TotalMilliseconds);
    }

    /// <summary>The first keystroke pays for the pool's growth and the rest ride the spare worker it
    /// leaves behind, so latency alone stops seeing parked readers once the pool has inflated.</summary>
    [Test]
    public async Task Idle_pty_readers_hold_no_pool_workers() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var probe = await RunProbeAsync();

        await Assert.That(probe.PoolThreads).IsLessThan(probe.Ptys);
    }

    static async Task<ProbeResult> RunProbeAsync() {
        using var host    = NativeTestHostProcess.Start("pty-input-latency");
        using var timeout = new CancellationTokenSource(ProbeTimeout);

        string output;

        try {
            output = await host.StandardOutput.ReadToEndAsync(timeout.Token);
            await host.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            host.Kill();

            throw new TimeoutException($"pty-input-latency probe did not finish within {ProbeTimeout}");
        }

        if (host.ExitCode != 0)
            throw new InvalidOperationException($"pty-input-latency probe exited {host.ExitCode}: {output}");

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new(
            Ptys: int.Parse(Single(lines, "PTYS="), CultureInfo.InvariantCulture),
            PoolThreads: int.Parse(Single(lines, "POOL_THREADS="), CultureInfo.InvariantCulture),
            LatenciesMs: [.. Values(lines, "LATENCY_MS=").Select(v => double.Parse(v, CultureInfo.InvariantCulture))]
        );
    }

    static IEnumerable<string> Values(string[] lines, string key)
        => lines.Where(l => l.StartsWith(key, StringComparison.Ordinal)).Select(l => l[key.Length..]);

    static string Single(string[] lines, string key) => Values(lines, key).Single();

    sealed record ProbeResult(int Ptys, int PoolThreads, double[] LatenciesMs);
}
