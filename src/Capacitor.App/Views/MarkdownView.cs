using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// Takes a code block's text. Left unset on a surface with nowhere to run it, which is what
    /// withdraws the offer from the block.
    public static readonly StyledProperty<ICommand?> RunCodeProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(RunCode));

    // The extension builds its TextMate highlighters on first use and keeps them, so one
    // instance serves the app; a per-view instance rebuilds them on every render.
    static readonly TextMateExtension Highlighting = new();

    readonly MarkdownViewer _viewer = new();
    readonly DetailsState _details = new();
    readonly DetailsExtension _detailsExtension;
    readonly CodeBlockActions _codeActions;

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => {
            view._details.Clear();
            view.Render();
        });
        RunCodeProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        FlavorProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ApplyFlavor());
    }

    public MarkdownView() {
        _detailsExtension = new(_details, OnDetailsToggled);
        _codeActions = new(this);
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

    /// A code block reads RunCode as it is built, so a command arriving after the first render —
    /// the order a binding on this property lands in — needs the document built again. Clearing
    /// first is what makes that second build happen at all: the viewer renders on a change, and
    /// the markdown it already holds is not one.
    void Render() {
        if (_viewer.Markdown == Text) _viewer.Markdown = null;
        _viewer.Markdown = Text;
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

    public ICommand? RunCode {
        get => GetValue(RunCodeProperty);
        set => SetValue(RunCodeProperty, value);
    }

    void ApplyFlavor() {
        _details.Clear();
        _viewer.Extensions.Clear();
        if (Flavor == MarkdownFlavor.GitHub) {
            _viewer.Extensions.Add(KcapMarkdownExtension.GitHub);
            _viewer.Extensions.Add(_detailsExtension);
        } else {
            _viewer.Extensions.Add(KcapMarkdownExtension.Chat);
        }
        _viewer.Extensions.Add(Highlighting);
        // After the highlighter, whose code block renderer this one wraps.
        _viewer.Extensions.Add(_codeActions);
        // Only the pipeline change re-renders, so it goes last.
        _viewer.Pipeline = Flavor == MarkdownFlavor.GitHub ? GitHubPipeline.Instance : null;
    }

    /// Re-rendering rebuilds MarkView's selection index, so it always matches what is visible.
    /// The header that was pressed is gone with the old tree; its successor gets the focus back.
    void OnDetailsToggled(int ordinal, bool expanded) {
        _details.Set(ordinal, expanded);
        var hadFocus = Header(ordinal)?.IsFocused == true;
        Dispatcher.UIThread.Post(() => {
            Render();
            if (hadFocus) Header(ordinal)?.Focus();
        });
    }

    ToggleButton? Header(int ordinal) =>
        this.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button =>
            button.Classes.Contains("markdown-details-summary") && button.Tag is int tag && tag == ordinal);
}
