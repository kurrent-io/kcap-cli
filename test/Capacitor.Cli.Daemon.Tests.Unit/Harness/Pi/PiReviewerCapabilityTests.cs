using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiReviewerCapabilityTests {
    const string Floor = PiReviewerCapability.VerifiedFloor;

    [Test]
    public async Task Consent_short_circuits_before_everything() =>
        await Assert.That(PiReviewerCapability.Decide(posixHost: false, operatorEnabled: false, null, null))
            .IsEqualTo(PiReviewerDecision.Disabled);

    [Test]
    public async Task Windows_is_refused_before_any_version_is_looked_at() =>
        await Assert.That(PiReviewerCapability.Decide(false, true, "9.9.9", "9.9.9"))
            .IsEqualTo(PiReviewerDecision.UnsupportedPlatform);

    [Test]
    public async Task An_unresolvable_install_is_refused() =>
        await Assert.That(PiReviewerCapability.Decide(true, true, null, Floor))
            .IsEqualTo(PiReviewerDecision.VersionUnresolved);

    [Test]
    [Arguments("0.85.0")]
    [Arguments("0.84.9")]
    [Arguments("0.1.0")]
    public async Task A_build_below_the_verified_floor_is_refused_even_when_the_operator_affirmed_it(string installed) =>
        await Assert.That(PiReviewerCapability.Decide(true, true, installed, installed))
            .IsEqualTo(PiReviewerDecision.BelowVerifiedFloor);

    [Test]
    [Arguments("0.85.1")]
    [Arguments("0.85.2")]
    [Arguments("0.86.1")]
    [Arguments("1.0.0")]
    [Arguments("v2.3.4")]
    public async Task Any_build_at_or_above_both_floors_is_allowed(string installed) =>
        await Assert.That(PiReviewerCapability.Decide(true, true, installed, Floor))
            .IsEqualTo(PiReviewerDecision.Allowed);

    [Test]
    public async Task The_operator_floor_still_applies_above_the_verified_one() =>
        await Assert.That(PiReviewerCapability.Decide(true, true, "0.86.0", "0.87.0"))
            .IsEqualTo(PiReviewerDecision.VersionBelowMinimum);

    [Test]
    public async Task No_recorded_operator_minimum_is_its_own_refusal() =>
        await Assert.That(PiReviewerCapability.Decide(true, true, "0.86.0", null))
            .IsEqualTo(PiReviewerDecision.VersionNoMinimum);

    [Test]
    public async Task An_unorderable_install_is_incomparable_not_below() =>
        await Assert.That(PiReviewerCapability.Decide(true, true, "nightly", Floor))
            .IsEqualTo(PiReviewerDecision.VersionIncomparable);

    [Test]
    public async Task Every_denial_has_a_coded_reason_and_Allowed_has_none() {
        foreach (var decision in Enum.GetValues<PiReviewerDecision>().Where(d => d != PiReviewerDecision.Allowed))
            await Assert.That(PiReviewerCapability.DenialReason(decision, "0.84.0", "0.85.1", "pi")).StartsWith("pi_reviewer_");

        await Assert.That(() => PiReviewerCapability.DenialReason(PiReviewerDecision.Allowed, null, null, "pi"))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>The floor is a minimum. Nothing in the ladder may compare a version for equality.</summary>
    [Test]
    public async Task The_verified_floor_is_never_an_upper_bound() =>
        await Assert.That(PiReviewerCapability.Decide(true, true, "99.0.0", Floor)).IsEqualTo(PiReviewerDecision.Allowed);
}
