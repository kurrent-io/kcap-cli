using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A collapsed section renders its header and nothing else: the selection index is built once
/// per render and ignores visibility, so hidden content would be copied and would take clicks.
/// Only an outermost section is a card; one inside another is a header row with its content set
/// in beneath it, so nesting reads as an outline rather than boxes in boxes.
sealed class DetailsBlockRenderer(DetailsState state, Action<int, bool> toggle) : AvaloniaObjectRenderer<DetailsBlock> {
    static readonly Geometry Down = Geometry.Parse("M3,4.5 L6,7.5 L9,4.5");
    static readonly Geometry Right = Geometry.Parse("M4.5,3 L7.5,6 L4.5,9");

    protected override void Write(AvaloniaRenderer renderer, DetailsBlock obj) {
        var expanded = state.IsExpanded(obj.Ordinal, obj.StartsOpen);
        var nested = IsNested(obj);

        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("markdown-details-label");
        renderer.Push(label.Inlines!);
        if (obj.Summary.FirstChild is null) renderer.WriteInline(new Run("Details"));
        else renderer.WriteChildren(obj.Summary);
        renderer.Pop();

        var chevron = new Avalonia.Controls.Shapes.Path { Data = expanded ? Down : Right, Width = 12, Height = 12, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center };
        chevron.Classes.Add("markdown-details-chevron");
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 9 };
        row.Children.Add(chevron);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var header = new ToggleButton { Content = row, IsChecked = expanded, Tag = obj.Ordinal };
        header.Classes.Add("markdown-details-summary");
        header.Click += (_, _) => toggle(obj.Ordinal, header.IsChecked == true);

        var panel = new StackPanel { Children = { header } };
        if (expanded) {
            var content = new StackPanel { Spacing = 8 };
            content.Classes.Add("markdown-details-content");
            renderer.Push(content);
            renderer.WriteChildren(obj);
            renderer.Pop();
            panel.Children.Add(content);
        }

        var border = new Border { Child = panel };
        border.Classes.Add("markdown-details");
        if (nested) border.Classes.Add("markdown-details-nested");
        renderer.WriteBlock(border);
    }

    static bool IsNested(DetailsBlock block) {
        for (var parent = block.Parent; parent is not null; parent = parent.Parent) if (parent is DetailsBlock) return true;
        return false;
    }
}
