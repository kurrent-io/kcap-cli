using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Views.Onboarding;

/// Fills a TextBlock from disclosure spans, setting code spans in the onboarding mono font.
public static class OnboardingSpans {
    public static readonly AttachedProperty<IReadOnlyList<DisclosureSpan>?> SpansProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<DisclosureSpan>?>("Spans", typeof(OnboardingSpans));

    static OnboardingSpans() {
        SpansProperty.Changed.AddClassHandler<TextBlock>((block, e) => Fill(block, e.NewValue as IReadOnlyList<DisclosureSpan>));
    }

    public static IReadOnlyList<DisclosureSpan>? GetSpans(TextBlock block) => block.GetValue(SpansProperty);
    public static void SetSpans(TextBlock block, IReadOnlyList<DisclosureSpan>? value) => block.SetValue(SpansProperty, value);

    static void Fill(TextBlock block, IReadOnlyList<DisclosureSpan>? spans) {
        var inlines = block.Inlines ??= [];
        inlines.Clear();
        if (spans is null) return;
        var mono = block.TryFindResource("OnboardingMonoFont", out var font) ? font as FontFamily : null;
        foreach (var span in spans) {
            var run = new Run(span.Text);
            if (span.Code && mono is not null) run.FontFamily = mono;
            inlines.Add(run);
        }
    }
}
