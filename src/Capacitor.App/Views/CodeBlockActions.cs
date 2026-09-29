using Avalonia.Controls;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.SyntaxHighlighting;

namespace Capacitor.App.Views;

/// Puts a hover strip on every code block. One instance per view, because the strip's buttons
/// answer to that view's own commands, and the strips of the document on screen are what a
/// command arriving later reaches.
public sealed class CodeBlockActions(MarkdownView view) : IMarkViewExtension {
    readonly List<(StackPanel Strip, string Text)> _strips = [];

    /// Syntax highlighting installs its own code block renderer in place of MarkView's, so that
    /// is the one to replace: replacing MarkView's would leave both registered and the renderer
    /// that runs is the first one accepting a code block, not the last one added.
    public void Register(AvaloniaRenderer renderer) {
        // A render builds a new document, and the strips of the old one leave with it.
        _strips.Clear();
        renderer.ReplaceOrAdd<TextMateCodeBlockRenderer>(new CodeBlockActionRenderer(view, this));
    }

    internal void Track(StackPanel strip, string text) => _strips.Add((strip, text));

    /// A binding lands the command after the text has rendered, so the offer is added to or
    /// taken from the strips in place: building the document again for it would double the cost
    /// of every chat row the list realizes.
    internal void ApplyRunCode() {
        var command = view.RunCode;
        foreach (var (strip, text) in _strips) {
            var existing = strip.Children.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("markdown-code-run"));
            if (command is null || !CodeBlockActionRenderer.IsRunnable(text)) {
                if (existing is not null) strip.Children.Remove(existing);
            } else if (existing is null) {
                strip.Children.Add(CodeBlockActionRenderer.RunButton(command, text));
            } else {
                existing.Command = command;
            }
        }
    }
}
