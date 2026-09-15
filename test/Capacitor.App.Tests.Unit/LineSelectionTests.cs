using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// A triple click in a TextBox or SelectableTextBlock selects the logical line under the pointer,
/// not the whole text: Avalonia's own class handler answers the third click with SelectAll.
public class LineSelectionTests {
    [Test]
    [Arguments("ab\ncd\nef", 3, 3, 5)]
    [Arguments("ab\ncd\nef", 4, 3, 5)]
    [Arguments("ab\ncd\nef", 5, 3, 5)]
    [Arguments("ab\ncd\nef", 0, 0, 2)]
    [Arguments("ab\ncd\nef", 8, 6, 8)]
    [Arguments("one line", 3, 0, 8)]
    [Arguments("", 0, 0, 0)]
    [Arguments("a\r\nb", 3, 3, 4)]
    [Arguments("a\r\nb", 1, 0, 1)]
    public async Task A_line_runs_between_the_surrounding_newlines_excluding_them(string text, int index, int start, int end) {
        await Assert.That(LineSelection.LineBounds(text, index)).IsEqualTo((start, end));
    }

    [Test]
    public async Task An_index_past_the_text_clamps_to_the_last_line() {
        await Assert.That(LineSelection.LineBounds("ab\ncd", 40)).IsEqualTo((3, 5));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Triple_clicking_a_text_box_selects_the_line_under_the_pointer() {
        await RunOnUiAsync(async () => {
            var box = new TextBox { Text = "first line\nsecond line\nthird line", AcceptsReturn = true, FontSize = 14, Width = 300 };
            var window = Show(box);
            WarmUp(window, LineCenter(box, window, line: 0));
            var point = LineCenter(box, window, line: 1);
            TripleClick(window, point);
            await Assert.That(box.SelectedText).IsEqualTo("second line");
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Triple_clicking_a_selectable_text_block_selects_the_line_under_the_pointer() {
        await RunOnUiAsync(async () => {
            // Headless drawing paints no glyphs, so only a background makes the block hit-testable.
            var block = new SelectableTextBlock { Text = "first line\nsecond line\nthird line", FontSize = 14, Width = 300, Background = Brushes.Transparent };
            var window = Show(block);
            WarmUp(window, LineCenter(block, window, line: 0));
            var point = LineCenter(block, window, line: 2);
            TripleClick(window, point);
            await Assert.That(block.SelectedText).IsEqualTo("third line");
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_double_click_still_selects_the_word() {
        await RunOnUiAsync(async () => {
            var box = new TextBox { Text = "first line\nsecond line", AcceptsReturn = true, FontSize = 14, Width = 300 };
            var window = Show(box);
            WarmUp(window, LineCenter(box, window, line: 1));
            var point = LineCenter(box, window, line: 0);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(box.SelectedText).IsNotEqualTo("first line");
            await Assert.That(box.SelectedText).IsNotEmpty();
            window.Close();
        });
    }

    static Window Show(Control content) {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    /// The centre of a rendered line, in window coordinates, read off the control's own layout so
    /// the click lands on real glyphs rather than a guessed pixel row.
    static Point LineCenter(Control control, Window window, int line) {
        var (layout, host) = control switch {
            TextBox box => (box.GetVisualDescendants().OfType<TextPresenter>().First().TextLayout,
                (Visual)box.GetVisualDescendants().OfType<TextPresenter>().First()),
            SelectableTextBlock block => (block.TextLayout, (Visual)block),
            _ => throw new ArgumentException("unsupported", nameof(control))
        };
        var textLine = layout.TextLines[line];
        var y = layout.TextLines.Take(line).Sum(l => l.Height) + textLine.Height / 2;
        var local = new Point(Math.Max(2, textLine.Width / 2), y);
        return host.TranslatePoint(local, window) ?? throw new InvalidOperationException("line is not under the window");
    }

    /// The first press on a control pays a one-off JIT cost that can exceed the double-click window,
    /// so the counted clicks land on a line the warm-up did not touch, where the count restarts at one.
    static void WarmUp(Window window, Point point) {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    static void TripleClick(Window window, Point point) {
        for (var i = 0; i < 3; i++) {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
