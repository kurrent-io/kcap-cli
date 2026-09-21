namespace Capacitor.Cli.Daemon.Harness.Pi;

internal enum PiReviewerDecision {
    Allowed,
    Disabled,
    UnsupportedPlatform,
    VersionUnresolved,
    BelowVerifiedFloor,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}
