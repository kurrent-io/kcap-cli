using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseReviewerCapabilityTests {
    const string Floor = MuseReviewerCapability.VerifiedFloor;

    [Test]
    public async Task The_verified_floor_is_the_build_the_lane_was_measured_on() =>
        await Assert.That(Floor).IsEqualTo("1.4.7");

    [Test]
    public async Task Consent_short_circuits_before_everything() =>
        await Assert.That(MuseReviewerCapability.Decide(posixHost: false, operatorEnabled: false, null, null))
            .IsEqualTo(MuseReviewerDecision.Disabled);

    [Test]
    public async Task Windows_is_refused_before_any_version_is_looked_at() =>
        await Assert.That(MuseReviewerCapability.Decide(false, true, "9.9.9", "9.9.9"))
            .IsEqualTo(MuseReviewerDecision.UnsupportedPlatform);

    [Test]
    public async Task An_unresolvable_install_is_refused() =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, null, Floor))
            .IsEqualTo(MuseReviewerDecision.VersionUnresolved);

    [Test]
    [Arguments("1.4.6")]
    [Arguments("1.3.9")]
    [Arguments("0.9.0")]
    public async Task A_build_below_the_verified_floor_is_refused_even_when_the_operator_affirmed_it(string installed) =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, installed, installed))
            .IsEqualTo(MuseReviewerDecision.BelowVerifiedFloor);

    [Test]
    [Arguments("1.4.7")]
    [Arguments("1.4.8")]
    [Arguments("1.5.0")]
    [Arguments("2.0.0")]
    [Arguments("v1.4.7")]
    public async Task Any_build_at_or_above_both_floors_is_allowed(string installed) =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, installed, Floor))
            .IsEqualTo(MuseReviewerDecision.Allowed);

    [Test]
    public async Task The_operator_floor_still_applies_above_the_verified_one() =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, "1.5.0", "1.6.0"))
            .IsEqualTo(MuseReviewerDecision.VersionBelowMinimum);

    [Test]
    public async Task No_recorded_operator_minimum_is_its_own_refusal() =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, "1.5.0", null))
            .IsEqualTo(MuseReviewerDecision.VersionNoMinimum);

    [Test]
    public async Task An_unorderable_install_is_incomparable_not_below() =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, "nightly", Floor))
            .IsEqualTo(MuseReviewerDecision.VersionIncomparable);

    [Test]
    public async Task Every_denial_has_a_coded_reason_and_Allowed_has_none() {
        foreach (var decision in Enum.GetValues<MuseReviewerDecision>().Where(d => d != MuseReviewerDecision.Allowed))
            await Assert.That(MuseReviewerCapability.DenialReason(decision, "1.4.0", "1.4.7", "muse")).StartsWith("muse_reviewer_");

        await Assert.That(() => MuseReviewerCapability.DenialReason(MuseReviewerDecision.Allowed, null, null, "muse"))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task The_opt_out_reason_names_the_muse_switch() =>
        await Assert.That(MuseReviewerCapability.DenialReason(MuseReviewerDecision.Disabled, null, null, "muse"))
            .Contains("KCAP_MUSE_UNATTENDED_REVIEWER");

    /// <summary>The floor is a minimum. Nothing in the ladder may compare a version for equality.</summary>
    [Test]
    public async Task The_verified_floor_is_never_an_upper_bound() =>
        await Assert.That(MuseReviewerCapability.Decide(true, true, "99.0.0", Floor)).IsEqualTo(MuseReviewerDecision.Allowed);
}
