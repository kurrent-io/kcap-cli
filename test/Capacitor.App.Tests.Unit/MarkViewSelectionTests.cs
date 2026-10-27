using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

/// Pins the selection behaviour the app adds over MarkView's private selection layer. Each test
/// reaches that layer, so a MarkView release that renames a member it relies on fails here.
public class MarkViewSelectionTests {
    /// A view as wide as its text, the way a chat bubble sizes one.
    static (Window Window, MarkdownView View) ShowHugging(string markdown) {
        var view = new MarkdownView { Text = markdown, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window { Content = view, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view);
    }

    static TextBlock BlockReading(Visual root, string text) => All<TextBlock>(root).First(t => Reads(t) == text);

    static Point At(Window window, TextBlock block, double x) =>
        block.TranslatePoint(new Point(x, block.TextLayout.HitTestTextPosition(0).Height / 2), window)!.Value;

    static void Drag(Window window, params Point[] path) {
        window.MouseMove(path[0]);
        window.MouseDown(path[0], MouseButton.Left);
        foreach (var point in path[1..]) window.MouseMove(point, RawInputModifiers.LeftMouseButton);
        window.MouseUp(path[^1], MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    static async Task<string?> Copied(MarkdownView view) {
        await view.CopySelectionAsync();
        return await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync();
    }

    /// The last sample inside the block sits on the left half of the final glyph, where the
    /// caret is before it; the drag then leaves past the block's right edge.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drag_that_leaves_past_the_last_glyph_still_copies_it() {
        await RunOnUiAsync(async () => {
            var (window, view) = ShowHugging("hello world");
            try {
                var block = BlockReading(view, "hello world");
                var last = block.TextLayout.HitTestTextPosition(10);
                Drag(window, At(window, block, 1), At(window, block, 10), At(window, block, last.X + last.Width * 0.3),
                     At(window, block, block.Bounds.Width + 6));
                await Assert.That(await Copied(view)).IsEqualTo("hello world");
            } finally { window.Close(); }
        });
    }

    [Test]
    [Arguments("- alpha beta\n- gamma", "• alpha beta")]
    [Arguments("1. alpha beta\n2. gamma", "1. alpha beta")]
    [NotInParallel("AvaloniaSession")]
    public async Task Copying_a_whole_list_item_keeps_its_tail(string markdown, string expected) {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show(markdown);
            try {
                var block = BlockReading(view, "alpha beta");
                var end = block.TextLayout.HitTestTextPosition(10).X;
                Drag(window, At(window, block, 0.5), At(window, block, end / 2), At(window, block, end + 0.5));
                await Assert.That(await Copied(view)).IsEqualTo(expected);
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Part_of_a_list_item_copies_without_its_marker() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("- alpha beta\n- gamma");
            try {
                var block = BlockReading(view, "alpha beta");
                var from = block.TextLayout.HitTestTextPosition(6).X + 0.5;
                var end = block.TextLayout.HitTestTextPosition(10).X;
                Drag(window, At(window, block, from), At(window, block, end - 2), At(window, block, end + 0.5));
                await Assert.That(await Copied(view)).IsEqualTo("beta");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_double_click_selects_the_line_under_the_pointer() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("First paragraph here.\n\nSecond one, clicked.\n\nThird.");
            try {
                var point = At(window, BlockReading(view, "Second one, clicked."), 20);
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(await Copied(view)).IsEqualTo("Second one, clicked.");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_double_click_in_a_code_block_selects_one_line() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("```\nfirst line\nsecond line\n```");
            try {
                var block = All<TextBlock>(view).First(t => Reads(t).StartsWith("first line", StringComparison.Ordinal));
                var glyph = block.TextLayout.HitTestTextPosition(14);
                var point = block.TranslatePoint(new Point(glyph.X + block.Padding.Left + 1, glyph.Y + block.Padding.Top + glyph.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(await Copied(view)).IsEqualTo("second line");
            } finally { window.Close(); }
        });
    }

    /// The copy shortcut goes through the app's extraction, not MarkView's, which would drop the
    /// item's tail.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_copy_shortcut_copies_the_whole_list_item() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("- alpha beta");
            try {
                var block = BlockReading(view, "alpha beta");
                var end = block.TextLayout.HitTestTextPosition(10).X;
                Drag(window, At(window, block, 0.5), At(window, block, end / 2), At(window, block, end + 0.5));
                var copy = Application.Current!.PlatformSettings!.HotkeyConfiguration.Copy[0];
                window.KeyPressQwerty(PhysicalKey.C, (RawInputModifiers)copy.KeyModifiers);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo("• alpha beta");
            } finally { window.Close(); }
        });
    }

    [Test]
    [Arguments(0, false, "hello worl")]
    [Arguments(1, false, "second lin")]
    [Arguments(0, true, "hello worl")]
    [Arguments(1, true, "second lin")]
    [NotInParallel]
    public async Task A_drag_starting_in_left_padding_selects_from_the_nearest_character(int line, bool explicitCrlf, string expected) {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("```\r\nhello world\r\nsecond line\r\n```");
            try {
                if (explicitCrlf) {
                    var viewer = Viewer(view);
                    viewer.Extensions.Add(new CrlfCodeBlockRenderer());
                    viewer.Markdown = null;
                    viewer.Markdown = view.Text;
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                var block = All<TextBlock>(view).First(t => Reads(t).Contains("hello", StringComparison.Ordinal));
                await Assert.That(block.TextLayout.TextLines.Count).IsEqualTo(2);
                if (explicitCrlf) await Assert.That(block.TextLayout.TextLines[1].FirstTextSourceIndex).IsEqualTo(13);
                var offset = block.TextLayout.TextLines[line].FirstTextSourceIndex;
                var mid = block.TextLayout.HitTestTextPosition(offset);
                var origin = block.TranslatePoint(new Point(mid.X + block.Padding.Left, mid.Y + block.Padding.Top + mid.Height / 2), window)!.Value;
                var end = block.TranslatePoint(new Point(block.TextLayout.HitTestTextPosition(offset + 10).X + block.Padding.Left, mid.Y + block.Padding.Top + mid.Height / 2), window)!.Value;
                Drag(window, new Point(origin.X - 8, origin.Y), end);
                await Assert.That(await Copied(view)).IsEqualTo(expected);
            } finally { window.Close(); }
        });
    }

    [Test]
    [Arguments("above")]
    [Arguments("below")]
    [Arguments("left")]
    [Arguments("right")]
    [NotInParallel]
    public async Task A_drag_outside_code_line_padding_does_not_start_selection(string side) {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("```\nhello world\n```\n\nNearby text");
            try {
                var block = BlockReading(view, "hello world");
                var border = (Border)block.GetVisualParent()!;
                var origin = border.TranslatePoint(default, window)!.Value;
                var line = At(window, block, -8);
                var start = side switch {
                    "above" => new Point(line.X, origin.Y + 2),
                    "below" => new Point(line.X, origin.Y + border.Bounds.Height - 2),
                    "left" => new Point(origin.X - 8, line.Y),
                    _ => At(window, block, block.Bounds.Width + 8),
                };
                Drag(window, start, At(window, block, 30));
                await Assert.That(MarkViewSelection.HasSelection(Viewer(view))).IsFalse();
                await Assert.That(MarkViewSelection.IsSelecting(Viewer(view))).IsFalse();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel]
    public async Task Dragging_a_linked_block_image_does_not_select_nearby_text() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("[![picture](https://example.com/image.png)](https://example.com)\n\nNearby text", MarkdownFlavor.GitHub);
            try {
                var image = Images(view).Single();
                var start = image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), window)!.Value;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                await Assert.That(MarkViewSelection.IsSelecting(Viewer(view))).IsFalse();
                window.MouseMove(At(window, BlockReading(view, "Nearby text"), 30), RawInputModifiers.LeftMouseButton);
                await Assert.That(MarkViewSelection.HasSelection(Viewer(view))).IsFalse();
                window.MouseUp(start, MouseButton.Left);
            } finally { window.Close(); }
        });
    }
}
