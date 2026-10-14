using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Whether this daemon offers Muse Code as an unattended reviewer. Pure: the platform and both
/// versions are arguments, so every arm is reachable from any host.</summary>
internal static class MuseReviewerCapability {
    /// <summary>The lowest <c>muse</c> build the reviewer's posture flags and approval handling were
    /// measured on. A minimum, never an exact version; it sits beside the operator's recorded minimum
    /// because that one is seeded from whatever is installed.</summary>
    internal const string VerifiedFloor = "1.4.7";

    internal static MuseReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Consent first: a wedged muse must never be probed for a feature the operator switched off.
        if (!operatorEnabled) return MuseReviewerDecision.Disabled;

        if (!posixHost) return MuseReviewerDecision.UnsupportedPlatform;

        // No discard arm in either switch: a new comparison outcome must be a build error, not an admit.
        var againstVerified = ReviewerVersionAffirmations.Decide(installedVersion, VerifiedFloor) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => (MuseReviewerDecision?)null,
            ReviewerVersionAffirmation.Unresolved        => MuseReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.BelowMinimum      => MuseReviewerDecision.BelowVerifiedFloor,
            ReviewerVersionAffirmation.Incomparable      => MuseReviewerDecision.VersionIncomparable,
            ReviewerVersionAffirmation.NoMinimumRecorded => MuseReviewerDecision.VersionIncomparable
        };

        if (againstVerified is { } refused) return refused;

        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => MuseReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => MuseReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => MuseReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => MuseReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => MuseReviewerDecision.VersionIncomparable
        };
    }

    internal static string DenialReason(
            MuseReviewerDecision decision, string? installedVersion, string? minimumVersion, string binaryPath) =>
        decision switch {
            MuseReviewerDecision.Disabled =>
                "muse_reviewer_disabled: this daemon has explicitly disabled unattended Muse Code reviews. Unset "
              + "KCAP_MUSE_UNATTENDED_REVIEWER in the daemon's environment (not on the server) to restore the "
              + "default, which is enabled — or set it to 1.",

            MuseReviewerDecision.UnsupportedPlatform =>
                "muse_reviewer_unsupported_platform: unattended Muse Code reviews run only on macOS and Linux daemons.",

            MuseReviewerDecision.VersionUnresolved =>
                $"muse_reviewer_version_unresolved: the version of '{binaryPath}' could not be determined. "
              + $"Check that `muse --version` succeeds, and set {HarnessId.Muse.PathEnvVar} if it lives elsewhere. "
              + "A build that cannot be identified is refused rather than assumed compatible.",

            MuseReviewerDecision.BelowVerifiedFloor =>
                $"muse_reviewer_below_verified_floor: muse {Describe(installedVersion)} is older than {VerifiedFloor}, "
              + "the oldest build the reviewer's posture was measured on. Upgrade muse. Any newer build is accepted.",

            MuseReviewerDecision.VersionNoMinimum =>
                "muse_reviewer_version_no_minimum: this daemon has no recorded minimum muse version. A daemon "
              + "records one at startup when muse resolves, so the usual cause is a daemon that started before "
              + "muse was installed — restart it, or run `kcap daemon reviewer affirm --vendor muse`.",

            MuseReviewerDecision.VersionBelowMinimum =>
                $"muse_reviewer_version_below_minimum: muse {Describe(installedVersion)} is installed but this "
              + $"daemon's recorded minimum is {Describe(minimumVersion)}. Upgrade muse, or deliberately lower "
              + "the minimum to the installed build with `kcap daemon reviewer affirm --vendor muse`.",

            MuseReviewerDecision.VersionIncomparable =>
                $"muse_reviewer_version_incomparable: muse {Describe(installedVersion)} cannot be ordered against "
              + $"{VerifiedFloor} or this daemon's recorded minimum {Describe(minimumVersion)} as version numbers.",

            MuseReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason.")
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
