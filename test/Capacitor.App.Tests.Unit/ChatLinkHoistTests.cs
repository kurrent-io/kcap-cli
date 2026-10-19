using MarkView.Avalonia.Rendering.Inlines;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class ChatLinkHoistTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bold_markdown_link_is_clickable() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See **[docs](https://example.com/docs)** now.");
            try {
                var paragraph = Paragraphs(root).Single();
                var link = paragraph.Inlines!.OfType<MarkdownHyperlink>().Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://example.com/docs");
                Click(window, paragraph, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/docs" });
            } finally { window.Close(); }
        });
    }
}
