using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.SyntaxHighlighting;

namespace Capacitor.App.Views;

/// Puts a hover strip on every code block. One instance per view, because the strip's buttons
/// answer to that view's own commands.
public sealed class CodeBlockActions(MarkdownView view) : IMarkViewExtension {
    /// Syntax highlighting installs its own code block renderer in place of MarkView's, so that
    /// is the one to replace: replacing MarkView's would leave both registered and the renderer
    /// that runs is the first one accepting a code block, not the last one added.
    public void Register(AvaloniaRenderer renderer) =>
        renderer.ReplaceOrAdd<TextMateCodeBlockRenderer>(new CodeBlockActionRenderer(view));
}
