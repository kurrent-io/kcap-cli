using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class DetailsViewTests {
    const string OneSection = "<details>\n<summary><strong>Agent Prompt</strong></summary>\n\nhidden body\n\n</details>\n\nafter [v](https://visible.example)";

    static IEnumerable<ToggleButton> Headers(Visual root) => All<ToggleButton>(root).Where(b => b.Classes.Contains("markdown-details-summary"));

    static ToggleButton Header(Visual root, int ordinal) => Headers(root).Single(b => b.Tag is int tag && tag == ordinal);

    static string HeaderText(ToggleButton header) => Reads((TextBlock)header.Content!);

    /// A pointer click on the header's centre; a toggle re-renders, so callers re-query the tree.
    static void ClickHeader(Window window, ToggleButton header) {
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    static string SelectedText(Visual root) {
        var viewer = Viewer(root);
        viewer.SelectAll();
        return viewer.GetSelectedText();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_details_section_starts_collapsed_with_its_summary_as_the_header() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                var header = Header(root, 0);
                await Assert.That(header.IsChecked).IsFalse();
                await Assert.That(HeaderText(header)).Contains("Agent Prompt");
                await Assert.That(Spans<Avalonia.Controls.Documents.Bold>(((TextBlock)header.Content!).Inlines!).Count()).IsEqualTo(1);
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(t => t.Contains("hidden body"))).IsFalse();
                await Assert.That(All<Border>(root).Count(b => b.Classes.Contains("markdown-details"))).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins the selection semantics: collapsed content is not rendered, so select-all skips it;
    /// expanded, it is there; collapsed again, it is gone again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsed_content_is_absent_from_a_selection_and_returns_when_expanded() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                await Assert.That(SelectedText(root)).DoesNotContain("hidden body");
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(SelectedText(root)).Contains("hidden body");
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsFalse();
                await Assert.That(SelectedText(root)).DoesNotContain("hidden body");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Nested_sections_render_only_inside_an_expanded_parent() {
        await RunOnUiAsync(async () => {
            const string nested = "<details>\n<summary>Outer</summary>\n\n<details>\n<summary>Inner</summary>\n\ninner body\n\n</details>\n\n</details>";
            var (window, root, _) = Show(nested, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Headers(root).Count()).IsEqualTo(1);
                ClickHeader(window, Header(root, 0));
                await Assert.That(Headers(root).Count()).IsEqualTo(2);
                await Assert.That(SelectedText(root)).DoesNotContain("inner body");
                ClickHeader(window, Header(root, 1));
                await Assert.That(SelectedText(root)).Contains("inner body");
                ClickHeader(window, Header(root, 1));
                await Assert.That(SelectedText(root)).DoesNotContain("inner body");
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_section_starts_expanded_and_a_missing_summary_reads_Details() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("<details open>\n\nshown body\n\n</details>", MarkdownFlavor.GitHub);
            try {
                var header = Header(root, 0);
                await Assert.That(header.IsChecked).IsTrue();
                await Assert.That(HeaderText(header)).Contains("Details");
                await Assert.That(SelectedText(root)).Contains("shown body");
            } finally { window.Close(); }
        });
    }

    /// A stale index entry for hidden content would take this click; the re-render keeps the index
    /// true to what is visible, whether the section was never expanded or expanded and collapsed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_after_a_collapsed_section_opens_its_own_url() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://visible.example/" });

                ClickHeader(window, Header(root, 0));
                ClickHeader(window, Header(root, 0));
                (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://visible.example/", "https://visible.example/" });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_header_toggles_on_space_and_keeps_focus_across_the_re_render() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                Header(root, 0).Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(SelectedText(root)).Contains("hidden body");
                await Assert.That(TopLevel.GetTopLevel(root)!.FocusManager!.GetFocusedElement()).IsEqualTo(Header(root, 0));
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_the_text_resets_the_expanded_state() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show(OneSection, MarkdownFlavor.GitHub);
            try {
                ClickHeader(window, Header(view, 0));
                await Assert.That(Header(view, 0).IsChecked).IsTrue();
                view.Text = "<details>\n<summary>Other</summary>\n\nother body\n\n</details>";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Header(view, 0).IsChecked).IsFalse();
                await Assert.That(HeaderText(Header(view, 0))).Contains("Other");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_press_on_a_summary_link_toggles_and_opens_nothing() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("<details>\n<summary><a href=\"https://s.example\">S</a></summary>\n\nbody\n\n</details>", MarkdownFlavor.GitHub);
            try {
                await Assert.That(AllLinks(root)).IsEmpty();
                ClickHeader(window, Header(root, 0));
                await Assert.That(Header(root, 0).IsChecked).IsTrue();
                await Assert.That(opened).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// Pins the pre's shape against the list-item walker once more, this time inside an expanded
    /// details inside the item.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pre_inside_an_expanded_section_in_a_list_item_is_selectable() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("- <details open>\n  <summary>S</summary>\n\n  <pre>\n  alpha\n  beta\n  </pre>\n\n  </details>", MarkdownFlavor.GitHub);
            try {
                var selected = SelectedText(root);
                await Assert.That(selected).Contains("alpha");
                await Assert.That(selected).Contains("beta");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(60_000)]
    public async Task Adversarial_nesting_renders_without_throwing(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var details = string.Concat(Enumerable.Repeat("<details open>\n\n", 50)) + "x\n\n" + string.Concat(Enumerable.Repeat("</details>\n\n", 50));
            var (window, root, _) = Show(details, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Headers(root).Count()).IsEqualTo(GitHubHtmlPass.MaxDetailsNesting);
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(t => t.Contains('x'))).IsTrue();
            } finally { window.Close(); }

            var bold = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200));
            var (boldWindow, boldRoot, _) = Show(bold, MarkdownFlavor.GitHub);
            try {
                await Assert.That(Paragraphs(boldRoot).Single().Bounds.Height).IsGreaterThan(0);
            } finally { boldWindow.Close(); }
        });
    }
}
