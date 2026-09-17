using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit;

public class FeedbackMessageComposerTests {
    const string Trailer = "Sent from Kurrent Capacitor Desktop 1.0.3 · daemon tonys-mbp 1.0.3";

    [Test]
    public async Task Compose_trims_then_joins_with_a_blank_line() =>
        await Assert.That(FeedbackMessageComposer.Compose("  It broke.  \n", Trailer))
            .IsEqualTo("It broke.\n\n" + Trailer);

    [Test]
    public async Task Remaining_counts_the_composed_message_against_the_cap() {
        var overhead = 2 + Trailer.Length;

        await Assert.That(FeedbackMessageComposer.Remaining("", Trailer)).IsEqualTo(8000 - overhead);
        await Assert.That(FeedbackMessageComposer.Remaining(new string('x', 8000 - overhead), Trailer)).IsEqualTo(0);
        await Assert.That(FeedbackMessageComposer.Remaining(new string('x', 8001 - overhead), Trailer)).IsEqualTo(-1);
    }

    [Test]
    public async Task Largest_accepted_user_text_composes_to_exactly_the_cap() {
        var largest = new string('x', 8000 - 2 - Trailer.Length);

        await Assert.That(FeedbackMessageComposer.Compose(largest, Trailer).Length).IsEqualTo(8000);
        await Assert.That(FeedbackMessageComposer.Compose(largest + "x", Trailer).Length).IsEqualTo(8001);
    }
}
