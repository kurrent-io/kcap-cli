using Avalonia.Controls.Documents;
using Avalonia.Media;
using MarkView.Avalonia.Rendering.Inlines;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class ChatLinkHoistTests {
    [Test]
    [Arguments("See **[docs](https://example.com/docs)** now.")]
    [Arguments("See **<https://example.com/docs>** now.")]
    [Arguments("See **https://example.com/docs** now.")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bold_markdown_link_is_clickable(string markdown) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown);
            try {
                var paragraph = Paragraphs(root).Single();
                var link = paragraph.Inlines!.OfType<MarkdownHyperlink>().Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://example.com/docs");
                Click(window, paragraph, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/docs" });
            } finally { window.Close(); }
        });
    }
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_emphasized_image_placeholder_keeps_its_style() => RunOnUiAsync(async () => {
        var (window, root, _) = Show("**![badge](https://example.com/image.png)**");
        try {
            var bold = Paragraphs(root).Single().Inlines!.OfType<Bold>().Single();
            await Assert.That(bold.Inlines.Text).IsEqualTo("![badge](https://example.com/image.png)");
            await Assert.That(bold.FontWeight).IsEqualTo(FontWeight.Bold);
        } finally { window.Close(); }
    });
}
