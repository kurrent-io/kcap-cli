using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Capacitor.App.GitHubHtml;
using MarkView.Avalonia;
using MarkView.Avalonia.SyntaxHighlighting;

namespace Capacitor.App.Views;

/// Markdown rendered through MarkView under the app's link policy, image rule and palette.
public sealed class MarkdownView : ContentControl {
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Text));

    public static readonly StyledProperty<ICommand?> OpenLinkProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(OpenLink));

    public static readonly StyledProperty<MarkdownFlavor> FlavorProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownFlavor>(nameof(Flavor));

    // The extension builds its TextMate highlighters on first use and keeps them, so one
    // instance serves the app; a per-view instance rebuilds them on every render.
    static readonly TextMateExtension Highlighting = new();

    readonly MarkdownViewer _viewer = new();

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view._viewer.Markdown = view.Text);
        FlavorProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ApplyFlavor());
    }

    public MarkdownView() {
        ApplyFlavor();
        // The viewer's template owns a ScrollViewer; the list around it is what scrolls.
        ScrollViewer.SetVerticalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        _viewer.LinkClicked += (_, e) => {
            e.Handled = true;
            if (OpenLink is { } open && open.CanExecute(e.Url)) open.Execute(e.Url);
        };
        Content = _viewer;
    }

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? OpenLink {
        get => GetValue(OpenLinkProperty);
        set => SetValue(OpenLinkProperty, value);
    }

    public MarkdownFlavor Flavor {
        get => GetValue(FlavorProperty);
        set => SetValue(FlavorProperty, value);
    }

    void ApplyFlavor() {
        _viewer.Extensions.Clear();
        _viewer.Extensions.Add(Flavor == MarkdownFlavor.GitHub ? KcapMarkdownExtension.GitHub : KcapMarkdownExtension.Chat);
        _viewer.Extensions.Add(Highlighting);
        // Only the pipeline change re-renders, so it goes last.
        _viewer.Pipeline = Flavor == MarkdownFlavor.GitHub ? GitHubPipeline.Instance : null;
    }
}
