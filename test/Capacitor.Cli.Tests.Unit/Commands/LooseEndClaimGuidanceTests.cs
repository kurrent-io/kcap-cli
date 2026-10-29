namespace Capacitor.Cli.Tests.Unit.Commands;

public class LooseEndClaimGuidanceTests {
    static string Skill(string name) => File.ReadAllText(Path.Combine(RepoTree.Root(), "kcap", "skills", name, "SKILL.md"));

    [Test]
    public async Task Ordinary_pickup_and_abandonment_have_distinct_tools_from_completion() {
        var text = Skill("work-items");
        await Assert.That(text).Contains("claim_loose_end");
        await Assert.That(text).Contains("release_loose_end");
        await Assert.That(text).Contains("claim_id");
        await Assert.That(text).Contains("claimed_session_id");
        await Assert.That(text).Contains("recorded_catching_up");
    }

    [Test]
    public async Task Hosted_launch_guidance_does_not_equate_pending_with_sent() {
        var text = Skill("start-agents");
        await Assert.That(text).Contains("`pending`");
        await Assert.That(text).Contains("`not_sent`");
        await Assert.That(text).Contains("`unknown`");
        await Assert.That(text).Contains("loose_end_claim_id");
        await Assert.That(text).DoesNotContain("Each call answers `requested`");
    }

    [Test]
    public async Task Continuing_a_session_requires_reading_the_per_claim_outcomes() {
        var text = Skill("recap");
        await Assert.That(text).Contains("loose_end_claims");
        await Assert.That(text).Contains("attempted_claim_id");
        await Assert.That(text).Contains("ownership_lost");
        await Assert.That(text).Contains("unsupported");
        await Assert.That(text).Contains("incomplete");
    }
}
