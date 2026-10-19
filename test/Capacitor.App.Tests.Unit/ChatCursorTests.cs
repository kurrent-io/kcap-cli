using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class ChatCursorTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Prose_uses_the_text_cursor_and_a_link_uses_the_hand() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("See [docs](https://example.com/docs) now.");
            try {
                var paragraph = Paragraphs(root).Single();
                var viewer = Viewer(root);
                Move(window, paragraph, 0);
                await Assert.That(viewer.Cursor?.ToString()).IsEqualTo("Ibeam");
                Move(window, paragraph, StartOf(paragraph, Links(root).Single()));
                await Assert.That(viewer.Cursor?.ToString()).IsEqualTo("Hand");
            } finally { window.Close(); }
        });
    }

    static void Move(Window window, TextBlock block, int index) {
        var glyph = block.TextLayout.HitTestTextPosition(index);
        var point = block.TranslatePoint(new Point(glyph.X + 2, glyph.Y + glyph.Height / 2), window)!.Value;
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
    }
}
