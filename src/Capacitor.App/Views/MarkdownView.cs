using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering.Inlines;
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

    /// The repository a GitHub-flavoured body belongs to, as its `https://host/owner/repo` URL: its
    /// references in text link into it. Unset, they stay text.
    public static readonly StyledProperty<string?> ReferenceBaseProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(ReferenceBase));

    /// Titles for links to the app server's agent pages. Inherited, so the window sets it once for
    /// every view inside it; unset, those links keep their URL.
    public static readonly AttachedProperty<AgentLinkLabels?> AgentLinksProperty =
        AvaloniaProperty.RegisterAttached<MarkdownView, Control, AgentLinkLabels?>("AgentLinks", inherits: true);

    public static void SetAgentLinks(Control control, AgentLinkLabels? value) => control.SetValue(AgentLinksProperty, value);

    /// Takes a code block's text. Left unset on a surface with nowhere to run it, which is what
    /// withdraws the offer from the block.
    public static readonly StyledProperty<ICommand?> RunCodeProperty =
        AvaloniaProperty.Register<MarkdownView, ICommand?>(nameof(RunCode));

    // The extension builds its TextMate highlighters on first use and keeps them, so one
    // instance serves the app; a per-view instance rebuilds them on every render.
    static readonly TextMateExtension Highlighting = new();
    static readonly Cursor IBeamCursor = new(StandardCursorType.Ibeam);
    static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    readonly MarkdownViewer _viewer = new();
    ChatTabView? _selectionHost;
    readonly DetailsState _details = new();
    readonly DetailsExtension _detailsExtension;
    readonly CodeBlockActions _codeActions;
    readonly ReferenceLinks _referenceLinks;
    readonly AgentLinkTitles _agentLinkTitles;
    // Agents linked under their short-id stand-in, whose title arriving redraws the view.
    readonly HashSet<string> _untitledAgents = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> _unsettledAgentLinks = new(StringComparer.Ordinal);
    AgentLinkLabels? _watchedLinks;
    bool _attached;
    IDisposable? _titledWatch;
    readonly MarkdownLinkKeys _linkKeys;
    bool _nonLeftRelease;

    static MarkdownView() {
        TextProperty.Changed.AddClassHandler<MarkdownView>((view, _) => {
            view._details.Clear();
            view.Render();
        });
        RunCodeProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view._codeActions.ApplyRunCode());
        FlavorProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ApplyFlavor());
        ReferenceBaseProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        AgentLinksProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.RelabelAgentLinks());
    }

    public MarkdownView() {
        _detailsExtension = new(_details, OnDetailsToggled);
        _codeActions = new(this);
        _referenceLinks = new(this);
        _agentLinkTitles = new(this);
        _linkKeys = MarkdownLinkKeys.Attach(_viewer, () => OpenLink);
        ApplyFlavor();
        // The viewer's template owns a ScrollViewer; the list around it is what scrolls.
        ScrollViewer.SetVerticalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer, ScrollBarVisibility.Disabled);
        // MarkView opens a link on any button's release, so a non-left release is suppressed here:
        // a right-click belongs to the context menu, and launching the browser under it hangs macOS.
        AddHandler(PointerReleasedEvent, (_, e) => _nonLeftRelease = e.InitialPressMouseButton != MouseButton.Left,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _nonLeftRelease = false, RoutingStrategies.Bubble, handledEventsToo: true);
        _viewer.LinkClicked += (_, e) => {
            e.Handled = true;
            if (_nonLeftRelease) return;
            if (OpenLink is { } open && open.CanExecute(e.Url)) open.Execute(e.Url);
        };
        AddHandler(ContextRequestedEvent, (_, e) => ContextLinkUrl = LinkAt(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        MarkViewSelection.Attach(this, _viewer, CopySelectionAsync);
        // Bubble on the host runs after the viewer's own move, which has already chosen Hand or
        // the arrow. Prose takes the I-beam and a link the hand; a bare image stays on the arrow.
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnSelectionInput, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnSelectionInput, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnSelectionInput, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnSelectionInput, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnSelectionInput, RoutingStrategies.Bubble, handledEventsToo: true);
        ContextFlyout = TextContextMenu.ForMarkdown;
        Content = _viewer;
    }

    internal bool IsSelecting => Content is MarkdownViewer && MarkViewSelection.IsSelecting(_viewer);

    internal void ExtendSelection(Point pointInView) {
        if (Content is MarkdownViewer) MarkViewSelection.Extend(_viewer, pointInView);
        _selectionHost?.UpdateSelection(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _selectionHost = this.FindAncestorOfType<ChatTabView>();
        _selectionHost?.UpdateSelection(this);
        _attached = true;
        RelabelAgentLinks();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _selectionHost?.RemoveSelection(this);
        _selectionHost = null;
        _attached = false;
        WatchTitles();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property != ContentProperty) return;
        if (change.OldValue is SelectableTextBlock old) old.PropertyChanged -= OnPlainSelectionChanged;
        if (change.NewValue is SelectableTextBlock plain) plain.PropertyChanged += OnPlainSelectionChanged;
        _selectionHost?.UpdateSelection(this);
    }

    void OnPlainSelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (e.Property == SelectableTextBlock.SelectionStartProperty || e.Property == SelectableTextBlock.SelectionEndProperty)
            _selectionHost?.UpdateSelection(this);
    }

    void OnSelectionInput(object? sender, RoutedEventArgs e) => _selectionHost?.UpdateSelection(this);

    void OnPointerMoved(object? sender, PointerEventArgs e) {
        var link = FindHyperlinkAt(_viewer, e.GetPosition(_viewer));
        var image = ImageUnder(e.Source as Visual);
        var tip = link?.NavigateUri?.ToString() ?? image?.Target;
        if (!string.Equals(ToolTip.GetTip(_viewer) as string, tip, StringComparison.Ordinal))
            ToolTip.SetTip(_viewer, tip);
        // Cursor inherits, so the viewer's value is what a child with none of its own shows.
        _viewer.Cursor = link is not null || image?.Target is not null ? HandCursor
            : image is not null ? ArrowCursor
            : IBeamCursor;
    }

    /// The link a context menu was opened on: the one under the pointer, or the keyboard's current one.
    internal string? ContextLinkUrl { get; private set; }

    string? LinkAt(ContextRequestedEventArgs e) =>
        e.TryGetPosition(_viewer, out var point)
            ? FindHyperlinkAt(_viewer, point)?.NavigateUri?.ToString() ?? ImageUnder(e.Source as Visual)?.Target
            : _linkKeys.CurrentUrl;

    public async Task CopyLinkAsync() {
        if (ContextLinkUrl is not { } url || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(url);
    }

    static MarkdownImage? ImageUnder(Visual? visual) {
        for (; visual is not null && visual is not MarkdownViewer; visual = visual.GetVisualParent())
            if (visual is MarkdownImage image) return image;
        return null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FindHyperlinkAt")]
    static extern MarkdownHyperlink? FindHyperlinkAt(MarkdownViewer viewer, Point point);

    public bool HasSelection => Content is SelectableTextBlock plain
        ? !string.IsNullOrEmpty(plain.SelectedText)
        : MarkViewSelection.HasSelection(_viewer);

    /// The fallback's own Copy is fire-and-forget, so its clipboard write is awaited here instead.
    public async Task CopySelectionAsync() {
        if (Content is not SelectableTextBlock plain) {
            await MarkViewSelection.CopyAsync(_viewer);
            return;
        }
        if (plain.SelectedText is not { Length: > 0 } text || TopLevel.GetTopLevel(plain)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
    }

    internal void ClearSelection() {
        if (Content is SelectableTextBlock plain) plain.ClearSelection();
        else _viewer.ClearSelection();
        _selectionHost?.UpdateSelection(this);
    }

    public void SelectAll() {
        if (Content is SelectableTextBlock plain) plain.SelectAll();
        else _viewer.SelectAll();
        _selectionHost?.UpdateSelection(this);
    }

    /// Clearing first is what makes a build of the same text happen at all: the viewer renders on
    /// a change, and the markdown it already holds is not one.
    /// Markdig throws from the parse for text nested past its depth limit, with no type of its own to
    /// catch; that text shows as plain source, and the viewer is left holding nothing so a pipeline
    /// change cannot re-throw.
    void Render() {
        _linkKeys.Clear();
        _untitledAgents.Clear();
        _unsettledAgentLinks.Clear();
        if (ToolTip.GetTip(_viewer) is not null) ToolTip.SetTip(_viewer, null);
        if (_viewer.Markdown == Text) _viewer.Markdown = null;
        try {
            _viewer.Markdown = Text;
            Content = _viewer;
        } catch (ArgumentException e) when (e.Message.Contains("too deeply nested", StringComparison.Ordinal)) {
            _viewer.Markdown = null;
            Content = new SelectableTextBlock { Text = Text, TextWrapping = TextWrapping.Wrap };
        }
        _selectionHost?.UpdateSelection(this);
        WatchTitles();
    }

    internal string? AgentLinkLabel(string url) {
        var label = GetValue(AgentLinksProperty)?.For(url);
        if (label is not { IsTitle: true }) _unsettledAgentLinks[url] = label?.Text;
        if (label is { IsTitle: false }) _untitledAgents.Add(label.AgentId);
        return label?.Text;
    }

    /// The inherited labels come and go as the view is detached and reattached, and a title can land
    /// while it is away, so redraw only when a link it drew without a title would now read
    /// differently. A drawn title stays, and a view without labels keeps what it drew: it is
    /// detached, not shown elsewhere.
    void RelabelAgentLinks() {
        if (GetValue(AgentLinksProperty) is { } links && _unsettledAgentLinks.Any(link => links.For(link.Key)?.Text != link.Value)) Render();
        else WatchTitles();
    }

    /// Watches only while attached and holding a stand-in, so a chat of settled messages subscribes to nothing.
    void WatchTitles() {
        var links = _untitledAgents.Count == 0 || !_attached ? null : GetValue(AgentLinksProperty);
        if (ReferenceEquals(links, _watchedLinks)) return;
        _titledWatch?.Dispose();
        _watchedLinks = links;
        _titledWatch = links?.Titled.Subscribe(id => {
            if (_untitledAgents.Contains(id)) Render();
        });
    }

    internal string? KeyboardLinkUrl => _linkKeys.CurrentUrl;

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

    public string? ReferenceBase {
        get => GetValue(ReferenceBaseProperty);
        set => SetValue(ReferenceBaseProperty, value);
    }

    public ICommand? RunCode {
        get => GetValue(RunCodeProperty);
        set => SetValue(RunCodeProperty, value);
    }

    void ApplyFlavor() {
        _linkKeys.Clear();
        _details.Clear();
        _viewer.Extensions.Clear();
        if (Flavor == MarkdownFlavor.GitHub) {
            _viewer.Extensions.Add(KcapMarkdownExtension.GitHub);
            _viewer.Extensions.Add(_referenceLinks);
            _viewer.Extensions.Add(_detailsExtension);
        } else {
            _viewer.Extensions.Add(KcapMarkdownExtension.Chat);
        }
        _viewer.Extensions.Add(_agentLinkTitles);
        _viewer.Extensions.Add(Highlighting);
        // After the highlighter, whose code block renderer this one wraps.
        _viewer.Extensions.Add(_codeActions);
        // Only the pipeline change re-renders, so it goes last.
        _viewer.Pipeline = Flavor switch {
            MarkdownFlavor.GitHub => GitHubPipeline.Instance,
            MarkdownFlavor.Prompt => ChatPipeline.Prompt,
            _                     => ChatPipeline.Instance,
        };
        _selectionHost?.UpdateSelection(this);
    }

    /// Re-rendering rebuilds MarkView's selection index, so it always matches what is visible.
    /// The header that was pressed is gone with the old tree; its successor gets the focus back
    /// and is held at the viewport Y the reader pressed it at: the height change lands above or
    /// below it, and the scroll offset alone would let the header wander with it.
    void OnDetailsToggled(int ordinal, bool expanded) {
        _details.Set(ordinal, expanded);
        var header = Header(ordinal);
        var hadFocus = header?.IsFocused == true;
        var scroll = this.FindAncestorOfType<ScrollViewer>();
        var anchorY = scroll is not null ? header?.TranslatePoint(new Point(0, 0), scroll)?.Y : null;
        Dispatcher.UIThread.Post(() => {
            Render();
            if (hadFocus) Header(ordinal)?.Focus();
            if (anchorY is { } y && scroll is not null) new ToggleAnchor(scroll, () => Header(ordinal), y).Hold();
        });
    }

    /// Restores the header once the new tree is laid out, and once more after the frame's
    /// remaining layout has settled — then lets go, so a reader's own scroll a moment later is
    /// not undone.
    sealed class ToggleAnchor(ScrollViewer scroll, Func<ToggleButton?> header, double y) {
        public void Hold() {
            scroll.UpdateLayout();
            Restore();
            Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
        }

        void Restore() {
            if (header()?.TranslatePoint(new Point(0, 0), scroll) is not { } after) return;
            var delta = after.Y - y;
            if (Math.Abs(delta) >= 0.5) scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + delta);
        }
    }

    ToggleButton? Header(int ordinal) =>
        this.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(button =>
            button.Classes.Contains("markdown-details-summary") && button.Tag is int tag && tag == ordinal);
}
