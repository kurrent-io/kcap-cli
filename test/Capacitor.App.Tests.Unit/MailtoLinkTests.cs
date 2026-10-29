using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class MailtoLinkTests {
    [Test]
    public async Task Mailto_is_openable_and_other_schemes_are_not() {
        await Assert.That(LinkPolicy.IsOpenable("mailto:test@example.com")).IsTrue();
        await Assert.That(LinkPolicy.IsOpenable("https://example.com")).IsTrue();
        await Assert.That(LinkPolicy.IsOpenable("javascript:alert(1)")).IsFalse();
        await Assert.That(LinkPolicy.IsOpenable("data:text/plain,hi")).IsFalse();
        await Assert.That(LinkPolicy.IsOpenable("file:///etc/passwd")).IsFalse();
        await Assert.That(LinkPolicy.IsOpenable("kcap://session")).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hovering_a_link_shows_its_destination() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("See [kurrent](https://example.com/not-kurrent) now.");
            try {
                var paragraph = Paragraphs(root).Single();
                var link = Links(root).Single();
                var glyph = paragraph.TextLayout.HitTestTextPosition(StartOf(paragraph, link));
                var point = paragraph.TranslatePoint(new Point(glyph.X + 2, glyph.Y + glyph.Height / 2), window)!.Value;
                window.MouseMove(point);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetTip(Viewer(root)) as string).IsEqualTo("https://example.com/not-kurrent");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hovering_a_linked_image_shows_its_destination() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("[![badge](https://h/b.png)](https://outer.example)", MarkdownFlavor.GitHub);
            try {
                var image = Images(root).Single();
                var point = image.TranslatePoint(new Point(4, image.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetTip(Viewer(root)) as string).IsEqualTo("https://outer.example");
            } finally { window.Close(); }
        });
    }

    /// A re-render replaces the link under a still pointer, so the previous destination must not stay up.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_the_text_clears_the_link_tooltip() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("See [kurrent](https://example.com/not-kurrent) now.");
            try {
                var paragraph = Paragraphs(root).Single();
                var link = Links(root).Single();
                var glyph = paragraph.TextLayout.HitTestTextPosition(StartOf(paragraph, link));
                var point = paragraph.TranslatePoint(new Point(glyph.X + 2, glyph.Y + glyph.Height / 2), window)!.Value;
                window.MouseMove(point);
                Dispatcher.UIThread.RunJobs();
                root.Text = "plain";
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetTip(Viewer(root)) as string).IsNull();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_mailto_link_renders_as_a_link() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Write [mail](mailto:test@example.com) please.");
            try {
                await Assert.That(Links(root).Single().NavigateUri?.ToString()).IsEqualTo("mailto:test@example.com");
            } finally { window.Close(); }
        });
    }
}
