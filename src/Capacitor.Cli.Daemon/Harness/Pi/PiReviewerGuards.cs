namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>Null on the runtime means an interactive launch: no guard is active.</summary>
internal sealed record PiReviewerGuards(TimeSpan RoundLimit, TimeSpan AbortGrace) {
    internal static readonly TimeSpan DefaultAbortGrace = TimeSpan.FromSeconds(2);
}
