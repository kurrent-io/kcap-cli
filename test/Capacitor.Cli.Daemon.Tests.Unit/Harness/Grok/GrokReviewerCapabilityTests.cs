using Capacitor.Cli.Daemon.Harness.Grok;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

public class GrokReviewerCapabilityTests {
    [Test]
    public async Task The_operator_switch_is_checked_before_the_version() {
        await Assert.That(GrokReviewerCapability.Decide(operatorEnabled: false, installedVersion: null, minimumVersion: null))
            .IsEqualTo(GrokReviewerDecision.Disabled);
    }

    [Test]
    [Arguments("1.0.52", "1.0.52", nameof(GrokReviewerDecision.Allowed))]
    [Arguments("1.0.60", "1.0.52", nameof(GrokReviewerDecision.Allowed))]
    [Arguments("1.0.40", "1.0.52", nameof(GrokReviewerDecision.VersionBelowMinimum))]
    [Arguments("1.0.52", null,     nameof(GrokReviewerDecision.VersionNoMinimum))]
    [Arguments(null,     "1.0.52", nameof(GrokReviewerDecision.VersionUnresolved))]
    public async Task An_enabled_reviewer_needs_a_build_at_or_above_the_minimum(string? installed, string? minimum, string expected) {
        await Assert.That(GrokReviewerCapability.Decide(operatorEnabled: true, installed, minimum).ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task The_disabled_reason_names_the_switch() {
        await Assert.That(GrokReviewerCapability.DenialReason(GrokReviewerDecision.Disabled, null, null))
            .Contains("KCAP_GROK_UNATTENDED_REVIEWER");
    }

    [Test]
    public async Task An_allowed_decision_has_no_reason() {
        await Assert.That(() => GrokReviewerCapability.DenialReason(GrokReviewerDecision.Allowed, null, null))
            .Throws<ArgumentOutOfRangeException>();
    }
}
