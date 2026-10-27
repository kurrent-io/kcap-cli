using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackMailTests {
    sealed class RecordingLauncher(bool opens) : IBrowserLauncher {
        public List<string> Opened { get; } = [];
        public bool TryOpen(string url) { Opened.Add(url); return opens; }
    }

    static FeedbackSubmission Submission(string message, FeedbackCategory category = FeedbackCategory.Bug) =>
        new(category, message, Guid.NewGuid(), FeedbackSource.Cli);

    static string DecodedBody(string url) => Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);

    [Test]
    public async Task A_short_report_is_addressed_subjected_and_carried_whole_with_crlf_breaks() {
        var draft = FeedbackMail.Draft(FeedbackCategory.Bug, "It broke.\nTwice.", copied: true);

        await Assert.That(draft.CutShort).IsFalse();
        await Assert.That(draft.Url).StartsWith("mailto:team@kcap.ai?subject=kcap%20bug%20report&body=");
        await Assert.That(DecodedBody(draft.Url)).IsEqualTo("It broke.\r\nTwice.");
    }

    [Test]
    public async Task Feedback_gets_its_own_subject() =>
        await Assert.That(FeedbackMail.Draft(FeedbackCategory.Feedback, "Nice.", copied: false).Url)
            .Contains("subject=kcap%20feedback&");

    [Test]
    public async Task A_long_report_is_cut_to_fit_the_cap_and_points_at_the_clipboard_only_when_copied() {
        var body = string.Concat(Enumerable.Repeat("é ü & ? # ", 2000));

        var copied    = FeedbackMail.Draft(FeedbackCategory.Bug, body, copied: true);
        var notCopied = FeedbackMail.Draft(FeedbackCategory.Bug, body, copied: false);

        await Assert.That(copied.CutShort).IsTrue();
        await Assert.That(copied.Url.Length).IsLessThanOrEqualTo(FeedbackMail.MaxUrlLength);
        await Assert.That(DecodedBody(copied.Url)).Contains("on your clipboard");
        await Assert.That(DecodedBody(notCopied.Url)).DoesNotContain("clipboard");
        await Assert.That(body).StartsWith(DecodedBody(copied.Url)[..40]);
    }

    [Test]
    public async Task A_cut_never_splits_a_surrogate_pair() {
        var body  = string.Concat(Enumerable.Repeat("😀", 1500));
        var draft = FeedbackMail.Draft(FeedbackCategory.Bug, body, copied: false);

        await Assert.That(draft.CutShort).IsTrue();
        await Assert.That(DecodedBody(draft.Url)).DoesNotContain("�");
    }

    [Test]
    public async Task Send_copies_the_full_body_and_opens_the_draft() {
        var launcher   = new RecordingLauncher(opens: true);
        var copies     = new List<string>();
        var submission = Submission(new string('x', 3000));

        var outcome = FeedbackMail.Send(submission, launcher, text => { copies.Add(text); return true; });

        await Assert.That(outcome.Opened).IsTrue();
        await Assert.That(outcome.Copied).IsTrue();
        await Assert.That(outcome.Draft.CutShort).IsTrue();
        await Assert.That(copies).IsEquivalentTo([FeedbackMail.Body(submission)]);
        await Assert.That(launcher.Opened).IsEquivalentTo([outcome.Draft.Url]);
    }

    [Test]
    public async Task The_body_carries_the_client_after_the_message() =>
        await Assert.That(FeedbackMail.Body(Submission("  It broke.  "))).StartsWith("It broke.\n\n---\nkcap cli ");

    [Test]
    public async Task A_desktop_body_adds_only_the_os_to_its_own_trailer() =>
        await Assert.That(FeedbackMail.Body(new FeedbackSubmission(FeedbackCategory.Bug, "It broke.\n\nSent from kcap Desktop 1.0", Guid.NewGuid(), FeedbackSource.Desktop)))
            .IsEqualTo($"It broke.\n\nSent from kcap Desktop 1.0\n\n---\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
}
