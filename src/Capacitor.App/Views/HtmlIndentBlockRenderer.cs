using Avalonia.Controls;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A border around a panel: the shape MarkView's selection walkers descend.
sealed class HtmlIndentBlockRenderer : AvaloniaObjectRenderer<HtmlIndentBlock> {
    protected override void Write(AvaloniaRenderer renderer, HtmlIndentBlock obj) {
        var content = new StackPanel { Spacing = 8 };
        renderer.Push(content);
        renderer.WriteChildren(obj);
        renderer.Pop();

        var border = new Border { Child = content };
        border.Classes.Add("markdown-indent");
        renderer.WriteBlock(border);
    }
}
