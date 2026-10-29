namespace Capacitor.Cli.Daemon.Harness.Muse;

internal enum MuseReviewerDecision {
    Allowed,
    Disabled,
    UnsupportedPlatform,
    VersionUnresolved,
    BelowVerifiedFloor,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}
