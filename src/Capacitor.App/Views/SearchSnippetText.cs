using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Renders snippet runs as a TextBlock's inlines: matched runs SemiBold in KcapTextBrush, the
/// rest in the block's own foreground.
public static class SearchSnippetText {
    public static readonly AttachedProperty<IReadOnlyList<SnippetRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<SnippetRun>?>("Runs", typeof(SearchSnippetText));

    static SearchSnippetText() => RunsProperty.Changed.AddClassHandler<TextBlock>((block, _) => Render(block));

    public static IReadOnlyList<SnippetRun>? GetRuns(TextBlock block) => block.GetValue(RunsProperty);

    public static void SetRuns(TextBlock block, IReadOnlyList<SnippetRun>? value) => block.SetValue(RunsProperty, value);

    static void Render(TextBlock block) {
        var inlines = new InlineCollection();
        foreach (var run in GetRuns(block) ?? []) {
            var inline = new Run(run.Text);
            if (run.Matched) {
                inline.FontWeight = FontWeight.SemiBold;
                if (MatchBrush(block) is { } brush) inline.Foreground = brush;
            }
            inlines.Add(inline);
        }
        block.Inlines = inlines;
    }

    static IBrush? MatchBrush(TextBlock block) =>
        block.TryFindResource("KcapTextBrush", out var found) || Application.Current?.TryFindResource("KcapTextBrush", out found) == true
            ? found as IBrush
            : null;
}
