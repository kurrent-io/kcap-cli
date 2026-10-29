using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Capacitor.App.Views;
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bare_image_keeps_the_arrow_and_a_linked_image_keeps_the_hand() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(
                "See ![shot](https://h/x.png) here.\n\n[![badge](https://h/b.png)](https://outer.example)",
                MarkdownFlavor.GitHub);
            try {
                var bare = Images(root).Single(i => i.Target is null);
                var linked = Images(root).Single(i => i.Target is not null);
                var viewer = Viewer(root);
                MoveOver(window, bare);
                await Assert.That(bare.Cursor?.ToString()).IsEqualTo("Arrow");
                await Assert.That(viewer.Cursor?.ToString()).IsEqualTo("Arrow");
                MoveOver(window, linked);
                await Assert.That(linked.Cursor?.ToString()).IsEqualTo("Hand");
                await Assert.That(viewer.Cursor?.ToString()).IsEqualTo("Hand");
                Move(window, Paragraphs(root).Single(p => Reads(p).Contains("See")), 0);
                await Assert.That(viewer.Cursor?.ToString()).IsEqualTo("Ibeam");
            } finally { window.Close(); }
        });
    }

    static void MoveOver(Window window, Control control) {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
    }

    static void Move(Window window, TextBlock block, int index) {
        var glyph = block.TextLayout.HitTestTextPosition(index);
        var point = block.TranslatePoint(new Point(glyph.X + 2, glyph.Y + glyph.Height / 2), window)!.Value;
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
    }
}
