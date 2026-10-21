using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Capacitor.App.Views;
using Markdig.Syntax;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Tests.Unit;

/// Uses explicit CRLF runs so Windows code-line offsets are exercised on every platform.
sealed class CrlfCodeBlockRenderer : AvaloniaObjectRenderer<CodeBlock>, IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) => renderer.ReplaceOrAdd<CodeBlockActionRenderer>(this);

    protected override void Write(AvaloniaRenderer renderer, CodeBlock obj) {
        var text = new TextBlock();
        for (var i = 0; i < obj.Lines.Count; i++) {
            if (i > 0) text.Inlines!.Add(new Run("\r\n"));
            text.Inlines!.Add(new Run(obj.Lines.Lines[i].Slice.ToString()));
        }
        var border = new Border { Child = text };
        border.Classes.Add("markdown-code-block");
        renderer.WriteBlock(border);
    }
}
