using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// One colour per kind, shared by the card, the reader header and the rail: a running check
/// pulses grey, a conflict takes the warning colour, open and draft are muted, merged is success.
public class PullRequestStatusTests {
    [Test]
    public async Task A_pending_check_pulses() {
        var status = new PullRequestStatus("Checks pending", "pending");
        await Assert.That(status.IsPulsing).IsTrue();
        await Assert.That(status.IsWarning).IsFalse();
    }

    [Test]
    public async Task A_conflict_is_the_warning_colour() {
        var status = new PullRequestStatus("Merge conflicts", "conflict");
        await Assert.That(status.IsWarning).IsTrue();
        await Assert.That(status.IsPulsing).IsFalse();
    }

    /// A required review waits on people, not a pipeline, so it keeps the warning colour and does not pulse.
    [Test]
    public async Task A_required_review_is_the_warning_colour_and_still() {
        var status = new PullRequestStatus("Review required", "warning");
        await Assert.That(status.IsWarning).IsTrue();
        await Assert.That(status.IsPulsing).IsFalse();
        await Assert.That(status.Tip).Contains("required review");
    }

    [Test]
    public async Task A_status_tip_explains_when_detail_is_absent() {
        await Assert.That(new PullRequestStatus("Merged", "merged").Tip).Contains("merged");
        await Assert.That(new PullRequestStatus("Open", "open").Tip).Contains("open");
        await Assert.That(new PullRequestStatus("2 failed", "failure", "1 failed · 0 pending · 1 passed").Tip)
            .IsEqualTo("1 failed · 0 pending · 1 passed");
        await Assert.That(new PullRequestStatus("Checks passing", "success", "All checks have passed.").Tip)
            .IsEqualTo("All checks have passed.");
    }

    [Test]
    public async Task A_draft_is_still() {
        var status = new PullRequestStatus("Draft", "draft");
        await Assert.That(status.IsPulsing).IsFalse();
        await Assert.That(status.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Success_failure_and_merged_keep_their_colours() {
        await Assert.That(new PullRequestStatus("", "success").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "open").IsSuccess).IsFalse();
        await Assert.That(new PullRequestStatus("", "failure").IsDanger).IsTrue();
        await Assert.That(new PullRequestStatus("", "closed").IsDanger).IsTrue();
    }

    [Test]
    public async Task Checks_and_reviews_use_filled_discs_git_lifecycle_keeps_glyphs() {
        await Assert.That(new PullRequestStatus("", "success").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "failure").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "pending").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "pending").IsPulsing).IsTrue();
        await Assert.That(new PullRequestStatus("", "warning").UsesDiscIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "warning").IsWarning).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").UsesGlyphIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "open").UsesGlyphIcon).IsTrue();
        await Assert.That(new PullRequestStatus("", "conflict").UsesGlyphIcon).IsTrue();
    }
}
