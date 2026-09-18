using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A collapsed section renders its header and nothing else: the selection index is built once
/// per render and ignores visibility, so hidden content would be copied and would take clicks.
sealed class DetailsBlockRenderer(DetailsState state, Action<int, bool> toggle) : AvaloniaObjectRenderer<DetailsBlock> {
    protected override void Write(AvaloniaRenderer renderer, DetailsBlock obj) {
        var expanded = state.IsExpanded(obj.Ordinal, obj.StartsOpen);

        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.Classes.Add("markdown-details-label");
        renderer.Push(label.Inlines!);
        renderer.WriteInline(new Run(expanded ? "▾ " : "▸ "));
        if (obj.Summary.FirstChild is null) renderer.WriteInline(new Run("Details"));
        else renderer.WriteChildren(obj.Summary);
        renderer.Pop();

        var header = new ToggleButton { Content = label, IsChecked = expanded, Tag = obj.Ordinal };
        header.Classes.Add("markdown-details-summary");
        header.Click += (_, _) => toggle(obj.Ordinal, header.IsChecked == true);

        var panel = new StackPanel { Spacing = 8, Children = { header } };
        if (expanded) {
            var content = new StackPanel { Spacing = 8 };
            renderer.Push(content);
            renderer.WriteChildren(obj);
            renderer.Pop();
            panel.Children.Add(content);
        }

        var border = new Border { Child = panel };
        border.Classes.Add("markdown-details");
        renderer.WriteBlock(border);
    }
}
