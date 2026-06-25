namespace Capacitor.Cli.Daemon.Services;

/// <summary>A cheap on-disk binary fingerprint: size + last-write-time ticks.</summary>
public readonly record struct BinaryStat(long Size, long MtimeTicks);

/// <summary>Pure decision helpers for the restart coordinator (unit-tested in isolation).</summary>
public static class RestartDecision {
    /// <summary>
    /// True when the on-disk binary differs from the startup baseline. A null
    /// <paramref name="current"/> means a transient stat failure (binary briefly
    /// missing mid-install) — treated as "no change" so we skip the tick. A null
    /// baseline (couldn't stat at startup) disables detection.
    /// </summary>
    public static bool BinaryChanged(BinaryStat? baseline, BinaryStat? current) =>
        baseline is { } b && current is { } c && (b.Size != c.Size || b.MtimeTicks != c.MtimeTicks);

    /// <summary>Restart fires only when one is queued AND (the daemon is idle OR the request forced it).</summary>
    public static bool ShouldFire(bool pending, bool busy, bool force) => pending && (force || !busy);
}
