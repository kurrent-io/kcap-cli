using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Whether this daemon may run Grok Build as an unattended review-flow reviewer. Enabled by default,
/// like the other gated reviewers; the switch is an opt-out.
///
/// <para>The reviewer's guards are behaviour of the installed build: Grok honouring
/// <c>session/new</c>'s <c>yoloMode: false</c> and its <c>agentProfile</c> tool clamp, and naming an MCP
/// call by <c>rawInput.tool_name</c>. So a build older than the recorded minimum is refused, and an
/// upgrade needs no action.</para>
/// </summary>
internal static class GrokReviewerCapability {
    internal static GrokReviewerDecision Decide(bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Operator flag first and short-circuiting, so a wedged binary cannot stall a switched-off feature.
        if (!operatorEnabled) return GrokReviewerDecision.Disabled;

        // No discard: a later arm must be a build failure, not a silent admit.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => GrokReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => GrokReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => GrokReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => GrokReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => GrokReviewerDecision.VersionIncomparable
        };
    }

    internal static string DenialReason(GrokReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            GrokReviewerDecision.Disabled =>
                "grok_unattended_reviewer_disabled: this daemon has explicitly disabled Grok as an unattended "
              + "review-flow reviewer. Unset KCAP_GROK_UNATTENDED_REVIEWER in the daemon's environment (not on "
              + "the server) to restore the default, which is enabled, or set it to 1.",
            GrokReviewerDecision.VersionUnresolved =>
                "grok_reviewer_version_unresolved: the installed grok version could not be determined, so it "
              + "cannot be compared against this daemon's recorded minimum.",
            GrokReviewerDecision.VersionNoMinimum =>
                "grok_reviewer_version_no_minimum: this daemon has no recorded minimum grok version. A daemon "
              + "records one at startup, so check that `grok --version` succeeds for the daemon user and "
              + "restart, or run `kcap daemon reviewer affirm --vendor grok`.",
            GrokReviewerDecision.VersionIncomparable =>
                $"grok_reviewer_version_incomparable: grok {Describe(installedVersion)} and this daemon's recorded "
              + $"minimum {Describe(minimumVersion)} cannot be ordered. Record the installed build with "
              + "`kcap daemon reviewer affirm --vendor grok`.",
            GrokReviewerDecision.VersionBelowMinimum =>
                $"grok_reviewer_version_below_minimum: grok {Describe(installedVersion)} is installed but this "
              + $"daemon's recorded minimum is {Describe(minimumVersion)}. The reviewer depends on the build "
              + "asking before an edit or command, so an older build is refused. Upgrade grok, or lower "
              + "the minimum with `kcap daemon reviewer affirm --vendor grok`.",
            GrokReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(nameof(decision), decision, "Allowed is not a denial and has no reason.")
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
