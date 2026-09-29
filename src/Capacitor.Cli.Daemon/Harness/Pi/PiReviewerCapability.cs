using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// Whether this daemon offers Pi as an unattended reviewer. Pure: the platform and both versions are
/// arguments, so every arm is reachable from any host.
/// </summary>
internal static class PiReviewerCapability {
    /// <summary>The lowest <c>pi</c> build on which the launch flags this lane depends on were measured.
    /// A minimum, never an exact version: a newer build needs no re-measurement to stay eligible, and
    /// this only moves up, deliberately, when a needed behaviour is found to start later.
    ///
    /// <para>It exists beside the operator's recorded minimum because that one is seeded from whatever
    /// is installed. A build too old to know a flag refuses to start on its own; a build that knows the
    /// flag but gives it a weaker meaning would not.</para></summary>
    internal const string VerifiedFloor = "0.85.1";

    internal static PiReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Consent first and short-circuiting: a wedged pi must never be probed for a feature the
        // operator switched off.
        if (!operatorEnabled) return PiReviewerDecision.Disabled;

        // The launch directory holds the reviewer's transcript and cannot be made owner-only on Windows.
        if (!posixHost) return PiReviewerDecision.UnsupportedPlatform;

        // No discard arm in either switch: a new comparison outcome must be a build error, not an admit.
        var againstVerified = ReviewerVersionAffirmations.Decide(installedVersion, VerifiedFloor) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => (PiReviewerDecision?)null,
            ReviewerVersionAffirmation.Unresolved        => PiReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.BelowMinimum      => PiReviewerDecision.BelowVerifiedFloor,
            ReviewerVersionAffirmation.Incomparable      => PiReviewerDecision.VersionIncomparable,
            ReviewerVersionAffirmation.NoMinimumRecorded => PiReviewerDecision.VersionIncomparable
        };

        if (againstVerified is { } refused) return refused;

        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => PiReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => PiReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => PiReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => PiReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => PiReviewerDecision.VersionIncomparable
        };
    }

    internal static string DenialReason(
            PiReviewerDecision decision, string? installedVersion, string? minimumVersion, string binaryPath) =>
        decision switch {
            PiReviewerDecision.Disabled =>
                "pi_reviewer_disabled: this daemon has explicitly disabled unattended Pi reviews. Unset "
              + "KCAP_PI_UNATTENDED_REVIEWER in the daemon's environment (not on the server) to restore the "
              + "default, which is enabled — or set it to 1.",

            PiReviewerDecision.UnsupportedPlatform =>
                "pi_reviewer_unsupported_platform: the per-launch directory holds the reviewer's transcript "
              + "and cannot be created owner-only on Windows.",

            PiReviewerDecision.VersionUnresolved =>
                $"pi_reviewer_version_unresolved: the version of '{binaryPath}' could not be determined. "
              + $"Check that `pi --version` succeeds, and set {HarnessId.Pi.PathEnvVar} if it lives elsewhere. "
              + "A build that cannot be identified is refused rather than assumed compatible.",

            PiReviewerDecision.BelowVerifiedFloor =>
                $"pi_reviewer_below_verified_floor: pi {Describe(installedVersion)} is older than {VerifiedFloor}, "
              + "the oldest build the reviewer's containment flags were measured on. Upgrade pi. Any newer "
              + "build is accepted.",

            PiReviewerDecision.VersionNoMinimum =>
                "pi_reviewer_version_no_minimum: this daemon has no recorded minimum pi version. A daemon "
              + "records one at startup when pi resolves, so the usual cause is a daemon that started before "
              + "pi was installed — restart it, or run `kcap daemon reviewer affirm --vendor pi`.",

            PiReviewerDecision.VersionBelowMinimum =>
                $"pi_reviewer_version_below_minimum: pi {Describe(installedVersion)} is installed but this "
              + $"daemon's recorded minimum is {Describe(minimumVersion)}. Upgrade pi, or deliberately lower "
              + "the minimum to the installed build with `kcap daemon reviewer affirm --vendor pi`.",

            PiReviewerDecision.VersionIncomparable =>
                $"pi_reviewer_version_incomparable: pi {Describe(installedVersion)} cannot be ordered against "
              + $"{VerifiedFloor} or this daemon's recorded minimum {Describe(minimumVersion)} as version numbers.",

            PiReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason.")
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
