namespace Capacitor.Cli.Daemon.Services;

/// <summary>A cheap on-disk binary fingerprint: size + last-write-time ticks, plus the version directory a
/// script install's <c>current</c> links to, since two releases' binaries can match on size and time.</summary>
public readonly record struct BinaryStat(long Size, long MtimeTicks, string? Target = null);

/// <summary>Pure decision helpers for the restart coordinator (unit-tested in isolation).</summary>
public static class RestartDecision {
    /// <summary>
    /// True when the on-disk binary differs from the startup baseline. A null
    /// <paramref name="current"/> means a transient stat failure (binary briefly
    /// missing mid-install) — treated as "no change" so we skip the tick. A null
    /// baseline (couldn't stat at startup) disables detection.
    /// </summary>
    public static bool BinaryChanged(BinaryStat? baseline, BinaryStat? current) =>
        baseline is { } b && current is { } c && b != c;

    /// <summary>
    /// Fires when a restart is queued and the daemon is not busy. A self-detected restart also waits
    /// for a turn boundary, up to the wait bound; an explicit or forced one never waits.
    /// </summary>
    public static bool ShouldFire(bool pending, bool busy, bool force, bool selfDetected, bool atTurnBoundary, bool waitExpired) =>
        pending && !busy && (force || !selfDetected || atTurnBoundary || waitExpired);
}
