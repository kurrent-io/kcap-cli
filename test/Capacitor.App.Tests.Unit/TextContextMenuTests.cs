using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

/// Right-clicking text opens the Kcap panel, not Fluent's MenuFlyout, and its rows act on the
/// control that was clicked.
public class TextContextMenuTests {
    static Window ShowIn(Control content) {
        var window = new Window { Content = content, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    static FlyoutPresenter RightClick(Window window, Control target, Control owner) {
        var point = target.TranslatePoint(new Point(4, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        return owner.ContextFlyout is Flyout { IsOpen: true, Content: Control { Parent: FlyoutPresenter presenter } }
            ? presenter
            : throw new InvalidOperationException("no flyout opened");
    }

    static Button Row(FlyoutPresenter presenter, string label) =>
        presenter.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string text && text == label);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_boxes_and_selectable_text_take_the_kcap_menu() {
        await RunOnUiAsync(async () => {
            var box = new TextBox { Text = "typed" };
            var block = new SelectableTextBlock { Text = "shown" };
            var window = ShowIn(new StackPanel { Children = { box, block } });
            try {
                await Assert.That(box.ContextFlyout).IsSameReferenceAs(TextContextMenu.ForTextBox);
                await Assert.That(block.ContextFlyout).IsSameReferenceAs(TextContextMenu.ForSelectableTextBlock);
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Right_clicking_chat_text_offers_copy_of_the_selection() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("Some chat text.");
            try {
                view.SelectAll();
                var presenter = RightClick(window, Paragraphs(view).Single(), view);
                await Assert.That(presenter.Classes).Contains("kcapPanel");
                var copy = Row(presenter, "Copy");
                await Assert.That(copy.IsEnabled).IsTrue();
                copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo("Some chat text.");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Copy_is_withdrawn_while_nothing_is_selected() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("Some chat text.");
            try {
                var presenter = RightClick(window, Paragraphs(view).Single(), view);
                await Assert.That(Row(presenter, "Copy").IsEnabled).IsFalse();
                await Assert.That(Row(presenter, "Select all").IsEnabled).IsTrue();
            } finally { window.Close(); }
        });
    }
}
