namespace Capacitor.Cli.Daemon.Harness.Grok;

internal enum GrokReviewerDecision {
    Allowed,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}
