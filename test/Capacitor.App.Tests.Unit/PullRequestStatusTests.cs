using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// One colour per kind, shared by the card, the reader header and the rail: a running check
/// pulses grey, a conflict takes the warning colour, a draft is muted.
public class PullRequestStatusTests {
    [Test]
    public async Task A_pending_check_pulses_in_the_muted_colour() {
        var status = new PullRequestStatus("Checks pending", "pending");
        await Assert.That(status.IsPulsing).IsTrue();
        await Assert.That(status.IsMuted).IsTrue();
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
        await Assert.That(status.IsMuted).IsFalse();
    }

    [Test]
    public async Task A_draft_is_muted_and_still() {
        var status = new PullRequestStatus("Draft", "draft");
        await Assert.That(status.IsMuted).IsTrue();
        await Assert.That(status.IsPulsing).IsFalse();
        await Assert.That(status.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Success_failure_and_merged_keep_their_colours() {
        await Assert.That(new PullRequestStatus("", "success").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "open").IsSuccess).IsTrue();
        await Assert.That(new PullRequestStatus("", "failure").IsDanger).IsTrue();
        await Assert.That(new PullRequestStatus("", "closed").IsDanger).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").IsMuted).IsTrue();
        await Assert.That(new PullRequestStatus("", "merged").IsSuccess).IsFalse();
        await Assert.That(new PullRequestStatus("", "neutral").IsMuted).IsFalse();
    }
}
