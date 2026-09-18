namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Parse/render coverage for <see cref="PlanEntitlements"/>, the <c>X-Kcap-Plan</c> value. The
/// load-bearing property throughout is that anything not explicitly denied is ALLOWED, so a malformed
/// or absent header behaves as no header at all.
/// </summary>
public class PlanEntitlementsTests {
    [Test]
    public async Task Parse_blank_allows_everything() {
        foreach (var raw in new string?[] { null, "", "   " }) {
            var plan = PlanEntitlements.Parse(raw);
            await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsTrue();
            await Assert.That(plan.Denied).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_reads_a_denial() {
        var plan = PlanEntitlements.Parse("work_items=0,projects=0,analytics=1");

        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Projects)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Analytics)).IsTrue();
    }

    [Test]
    public async Task Parse_tolerates_whitespace_around_pairs() {
        var plan = PlanEntitlements.Parse(" work_items = 0 , projects=1 ");

        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Projects)).IsTrue();
    }

    [Test]
    public async Task Parse_skips_a_malformed_pair_without_losing_the_rest() {
        // One bad entry must not re-enable a feature a later pair denies.
        var plan = PlanEntitlements.Parse("garbage,=0,work_items=0");

        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
    }

    [Test]
    public async Task Parse_treats_any_non_zero_flag_as_allowed() {
        // Only "0" denies — an unrecognised flag must fail open, not guess.
        foreach (var flag in new[] { "1", "true", "yes", "" }) {
            var plan = PlanEntitlements.Parse($"work_items={flag}");
            await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_carries_an_unrecognised_feature_key() {
        // A newer server may deny something this CLI has no constant for; it still round-trips.
        var plan = PlanEntitlements.Parse("some_future_feature=0");

        await Assert.That(plan.Allows("some_future_feature")).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Feature_keys_are_case_insensitive() {
        var plan = PlanEntitlements.Parse("WORK_ITEMS=0");

        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
    }

    [Test]
    public async Task Render_round_trips_through_parse() {
        var rendered = PlanEntitlements.Parse("projects=0,work_items=0,analytics=1").Render();
        var plan     = PlanEntitlements.Parse(rendered);

        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Projects)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Analytics)).IsTrue();
    }

    [Test]
    public async Task Render_is_stable_regardless_of_input_order() {
        // The rendered form is the cache's dedupe key, so two spellings of one answer must not
        // look like two answers and rewrite the file on every response.
        await Assert.That(PlanEntitlements.Parse("work_items=0,projects=0").Render())
            .IsEqualTo(PlanEntitlements.Parse("projects=0,work_items=0").Render());
    }

    [Test]
    public async Task Parse_bounds_a_pathological_header() {
        var huge = string.Join(',', Enumerable.Range(0, 500).Select(i => $"f{i}=0"));

        await Assert.That(PlanEntitlements.Parse(huge).Denied.Count).IsLessThanOrEqualTo(32);
    }

    [Test]
    public async Task Parse_rejects_a_key_carrying_a_control_character() {
        var plan = PlanEntitlements.Parse("work\nitems=0");

        await Assert.That(plan.Denied).IsEmpty();
    }
}
