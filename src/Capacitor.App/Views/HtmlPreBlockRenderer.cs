using Avalonia.Controls;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A border around a panel around a selectable text block: the one shape both of MarkView's
/// selection walkers index, and the text block type it dispatches link clicks from.
sealed class HtmlPreBlockRenderer : AvaloniaObjectRenderer<HtmlPreBlock> {
    protected override void Write(AvaloniaRenderer renderer, HtmlPreBlock obj) {
        var text = new MarkdownSelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("markdown-pre-text");
        renderer.Push(text.Inlines!);
        renderer.WriteLeafInline(obj);
        renderer.Pop();

        var border = new Border { Child = new StackPanel { Children = { text } } };
        border.Classes.Add("markdown-code-block");
        border.Classes.Add("markdown-pre");
        renderer.WriteBlock(border);
    }
}
