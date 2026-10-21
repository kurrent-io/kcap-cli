using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.Views;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// Links in rendered markdown are inline spans that take no focus, so the viewer itself walks them:
/// Tab steps from link to link and then leaves, Enter or Space opens the current one through the
/// view's command, as a click does.
[NotInParallel("AvaloniaSession")]
public class MarkdownLinkKeyboardTests {
    const string Text = "See [first](https://example.com/a) and [second](https://example.com/b).";

    static (Window Window, MarkdownView View, Button Before, Button After, List<string> Opened) Show() {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var before = new Button { Content = "before" };
        var after  = new Button { Content = "after" };
        var view   = new MarkdownView { Text = Text, OpenLink = open, Width = 400 };
        var window = new Window { Content = new StackPanel { Children = { before, view, after } }, Width = 500, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        before.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, view, before, after, opened);
    }

    static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    static string? Url(MarkdownView view) => view.KeyboardLinkUrl;

    [Test]
    public Task Tab_walks_the_links_in_order_then_leaves_and_enter_or_space_opens_the_current_one() => RunOnUiAsync(async () => {
        var (window, view, _, after, opened) = Show();
        try {
            Press(window, PhysicalKey.Tab);
            await Assert.That(Url(view)).IsEqualTo("https://example.com/a");
            var block = MarkdownViewHarness.AllLinks(view).First().Block;
            await Assert.That(AdornerLayer.GetAdorner(block)).IsTypeOf<LinkFocusRing>()
                .Because("the current link is marked where it is drawn");

            Press(window, PhysicalKey.Enter);
            Press(window, PhysicalKey.Tab);
            await Assert.That(Url(view)).IsEqualTo("https://example.com/b");

            Press(window, PhysicalKey.Space);
            await Assert.That(string.Join(",", opened)).IsEqualTo("https://example.com/a,https://example.com/b");

            Press(window, PhysicalKey.Tab);
            await Assert.That(view.KeyboardLinkUrl).IsNull();
            await Assert.That(AdornerLayer.GetAdorner(block)).IsNull();
            await Assert.That(after.IsFocused).IsTrue().Because("past the last link, Tab moves on");
        } finally { window.Close(); }
    });

    [Test]
    public Task Shift_tab_enters_on_the_last_link_and_walks_back() => RunOnUiAsync(async () => {
        var (window, view, _, after, _) = Show();
        try {
            after.Focus();
            Dispatcher.UIThread.RunJobs();

            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            await Assert.That(Url(view)).IsEqualTo("https://example.com/b");

            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            await Assert.That(Url(view)).IsEqualTo("https://example.com/a");
        } finally { window.Close(); }
    });

    /// A code block's action is a focusable child of the viewer; tabbing onto it must leave its keys alone.
    [Test]
    public Task A_focused_child_control_keeps_its_own_keys() => RunOnUiAsync(async () => {
        var opened = new List<string>();
        var ran = new List<string>();
        var view = new MarkdownView {
            Text = "[first](https://example.com/a)\n\n```bash\n! kcap agent ls\n```",
            OpenLink = ReactiveCommand.Create<string>(opened.Add), RunCode = ReactiveCommand.Create<string>(ran.Add), Width = 400,
        };
        var window = new Window { Content = view, Width = 500, Height = 300 };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var run = MarkdownViewHarness.All<Button>(view).Single(b => b.Classes.Contains("markdown-code-run"));
            run.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Press(window, PhysicalKey.Enter);

            await Assert.That(view.KeyboardLinkUrl).IsNull();
            await Assert.That(opened).IsEmpty();
            await Assert.That(ran).IsEquivalentTo(new[] { "! kcap agent ls" });
        } finally { window.Close(); }
    });

    [Test]
    public Task The_ring_follows_the_link_when_its_text_rewraps() => RunOnUiAsync(async () => {
        var (window, view, _, _, _) = Show();
        try {
            Press(window, PhysicalKey.Tab);
            Press(window, PhysicalKey.Tab);
            var block = MarkdownViewHarness.AllLinks(view).First().Block;
            var ring = (LinkFocusRing)AdornerLayer.GetAdorner(block)!;
            var wide = ring.Rects[0];

            view.Width = 120;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(ring.Rects[0]).IsNotEqualTo(wide).Because("the narrower block wraps the second link");
        } finally { window.Close(); }
    });

    [Test]
    public Task A_flavor_change_drops_the_current_link() => RunOnUiAsync(async () => {
        var (window, view, _, _, _) = Show();
        try {
            Press(window, PhysicalKey.Tab);
            await Assert.That(view.KeyboardLinkUrl).IsNotNull();

            view.Flavor = MarkdownFlavor.GitHub;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(view.KeyboardLinkUrl).IsNull();
        } finally { window.Close(); }
    });

    [Test]
    public Task Tab_after_the_text_changes_starts_on_the_new_links() => RunOnUiAsync(async () => {
        var (window, view, _, _, _) = Show();
        try {
            Press(window, PhysicalKey.Tab);
            view.Text = "Now [third](https://example.com/c).";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(view.KeyboardLinkUrl).IsNull();

            Press(window, PhysicalKey.Tab);
            await Assert.That(Url(view)).IsEqualTo("https://example.com/c");
        } finally { window.Close(); }
    });

    [Test]
    public Task A_press_inside_the_viewer_drops_the_keyboard_link() => RunOnUiAsync(async () => {
        var (window, view, _, _, opened) = Show();
        try {
            var text = MarkdownViewHarness.Paragraphs(view).First();
            MarkdownViewHarness.ClickAt(window, text, 0); // on "See", not a link
            Press(window, PhysicalKey.Tab);
            await Assert.That(view.KeyboardLinkUrl).IsNotNull();

            MarkdownViewHarness.ClickAt(window, text, 0);
            Press(window, PhysicalKey.Enter);

            await Assert.That(view.KeyboardLinkUrl).IsNull();
            await Assert.That(opened).IsEmpty();
        } finally { window.Close(); }
    });

    /// A linked image renders as an image control rather than a span, and is one keyboard stop like a link,
    /// even inside a line of text where its hyperlink span holds nothing else.
    [Test]
    public Task A_linked_image_is_one_stop_and_opens_its_target() => RunOnUiAsync(async () => {
        var opened = new List<string>();
        var before = new Button { Content = "before" };
        var view = new MarkdownView {
            Text = "See [![badge](https://h/b.png)](https://outer.example) here.", Flavor = MarkdownFlavor.GitHub,
            OpenLink = ReactiveCommand.Create<string>(opened.Add), Width = 400,
        };
        var window = new Window { Content = new StackPanel { Children = { before, view } }, Width = 500, Height = 300 };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            before.Focus();
            Press(window, PhysicalKey.Tab);
            await Assert.That(Url(view)).IsEqualTo("https://outer.example");

            Press(window, PhysicalKey.Enter);
            await Assert.That(opened).IsEquivalentTo(new[] { "https://outer.example" });

            Press(window, PhysicalKey.Tab);
            await Assert.That(Url(view)).IsNull();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_pointer_focus_selects_no_link_and_enter_opens_nothing() => RunOnUiAsync(async () => {
        var (window, view, _, _, opened) = Show();
        try {
            MarkdownViewHarness.Viewer(view).Focus(NavigationMethod.Pointer);
            Dispatcher.UIThread.RunJobs();
            Press(window, PhysicalKey.Enter);

            await Assert.That(view.KeyboardLinkUrl).IsNull();
            await Assert.That(opened).IsEmpty();
        } finally { window.Close(); }
    });
}
