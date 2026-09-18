using Avalonia.Controls.Documents;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// Writes text as runs split at line ends. A line end inside a run never finishes laying out
/// under a height-unconstrained parent.
static class SourceText {
    public static void Write(AvaloniaRenderer renderer, string text) {
        var first = true;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) {
            if (!first) renderer.WriteInline(new LineBreak());
            first = false;
            if (line.Length > 0) renderer.WriteInline(new Run(line));
        }
    }
}
