using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackResultMessagesTests {
    [Test]
    public async Task Sent_has_no_refusal_sentence() =>
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Sent("a@b.c"))).IsNull();

    [Test]
    public async Task Each_refusal_has_its_sentence() {
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.NotConfigured()))
            .IsEqualTo("This server doesn't have support intake enabled.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Unavailable()))
            .IsEqualTo("Support intake isn't configured on this server — ask your admin.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.NoEmailOnFile()))
            .IsEqualTo("Your account has no email on file — sign in to the web app once, then retry.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.RateLimited()))
            .IsEqualTo("You've sent several reports recently — try again in a few minutes.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.TemporarilyUnavailable(TimeSpan.FromSeconds(7.2))))
            .IsEqualTo("Couldn't reach Kurrent support (temporary) — try again in 8s.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.TemporarilyUnavailable(null)))
            .IsEqualTo("Couldn't reach Kurrent support (temporary) — try again.");
        await Assert.That(FeedbackResultMessages.ForRefusal(new FeedbackResult.Invalid("Too long.")))
            .IsEqualTo("Too long.");
    }
}
