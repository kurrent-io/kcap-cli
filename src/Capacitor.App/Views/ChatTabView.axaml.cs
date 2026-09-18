using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail sticks to the bottom until the reader leaves it: a scroll change that lands above
/// the bottom is followed unless a gesture of the reader's own — pointer, wheel, key or scrollbar
/// inside the list — landed in the same layout pass, and any change that lands on the bottom
/// re-arms it.
/// The decision cannot be read from the change's deltas: the virtualizing panel reports the extent
/// as an estimate, and a realized row changing height makes it drop its anchor and re-place every
/// row from the average size, after which the presenter clamps or anchor-shifts the offset by
/// arbitrary amounts that look exactly like a reader scrolling up.
public partial class ChatTabView : UserControl {
    const double BottomTolerance = 2;

    ScrollViewer? _scroll;
    AttachmentDropPaste? _attachments;
    bool _followTail = true;
    /// Armed by a gesture inside the list and released once the dispatcher queue drains past
    /// layout, so it covers exactly the scroll changes that gesture produced — an expansion click
    /// or a wheel notch — and not the appends that land later.
    bool _readerGesture;
    /// The bubble the reader just toggled, kept at its viewport Y across the layout pass that
    /// changes its height. VirtualizingStackPanel otherwise drops its anchor and re-places every
    /// row from the average size, which jumps the reader off that bubble.
    object? _anchorItem;
    double _anchorY;
    bool _restoringAnchor;
    int _anchorPasses;
    bool _anchorLaidOut;

    public ChatTabView() {
        InitializeComponent();
        // Tunnel, not bubble: TextBox's own class handler runs first on the bubbling route, where
        // it inserts the newline and marks Enter handled before any instance handler sees it.
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // Press and release both: a click's command runs on the release, in a later dispatcher turn
        // than the press, by which time a flag armed on the press alone has been released.
        ChatItems.AddHandler(PointerPressedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerReleasedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerWheelChangedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(KeyDownEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Bubble-only events: a tunnel registration for these never fires.
        ChatItems.AddHandler(ScrollGestureEvent, OnReaderGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragDeltaEvent, OnReaderGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        // The ScrollViewer is the list template's; it exists only once the list is first measured,
        // which for a surface built before its first layout is later than the first rows.
        ChatItems.TemplateApplied += (_, _) => {
            var next = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (ReferenceEquals(next, _scroll)) return;
            DropToggleAnchor();
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = next;
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        };
    }

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

    /// Paired with the visual tree rather than the constructor: a tab swap detaches and re-attaches
    /// the same view, and a behaviour disposed on the way out has to come back with it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attachments ??= AttachmentDropPaste.Attach(
            ComposerCard, ComposerInput, AttachButton,
            () => (DataContext as ChatTabViewModel)?.Attachments, TimeProvider.System);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        DropToggleAnchor();
        _attachments?.Dispose();
        _attachments = null;
        base.OnDetachedFromVisualTree(e);
    }

    void OnReaderGesture(object? sender, RoutedEventArgs e) {
        // A TextBox in the list (Other…, free-text) is editing, not reading: arming follow-tail
        // would ScrollToEnd and recycle its virtualizing row, which drops the caret.
        if (OriginatesFromTextBox(e)) return;
        if (e.RoutedEvent == PointerPressedEvent) CaptureToggleAnchor(e.Source);
        if (_readerGesture) return;
        _readerGesture = true;
        Dispatcher.UIThread.Post(() => _readerGesture = false, DispatcherPriority.Background);
    }

    // Click is raised before the command flips the group, so a keyboard toggle (no pointer press
    // ahead of it) can still take its anchor here.
    void OnToolSummaryClick(object? sender, RoutedEventArgs e) {
        _followTail = false;
        if (_anchorItem is null) CaptureToggleAnchor(sender);
        ArmToggleRestore();
    }

    void CaptureToggleAnchor(object? source) {
        EnsureChatScroll();
        if (_scroll is null || ToolSummaryOf(source) is not { } summary) return;
        if (summary.TranslatePoint(new Point(0, 0), _scroll) is not { } point) return;
        _anchorItem = summary.DataContext;
        _anchorY = point.Y;
    }

    void EnsureChatScroll() {
        if (_scroll is not null) return;
        _scroll = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    void ArmToggleRestore() {
        if (_scroll is null || _anchorItem is null) return;
        _anchorPasses = 8;
        _anchorLaidOut = false;
        _scroll.LayoutUpdated -= OnAnchorLayout;
        _scroll.LayoutUpdated += OnAnchorLayout;
        Dispatcher.UIThread.Post(() => {
            _anchorLaidOut = true;
            RestoreToggleAnchor();
        }, DispatcherPriority.Render);
    }

    void OnAnchorLayout(object? sender, EventArgs e) {
        if (_restoringAnchor) return;
        _anchorLaidOut = true;
        RestoreToggleAnchor();
    }

    void RestoreToggleAnchor() {
        if (_scroll is null || _anchorItem is null) return;
        var summary = ToolSummaryFor(_anchorItem);
        if (summary is null) {
            if (!_anchorLaidOut) {
                FinishIfExhausted();
                return;
            }
            RealizeAnchor();
            summary = ToolSummaryFor(_anchorItem);
        }
        if (summary?.TranslatePoint(new Point(0, 0), _scroll) is not { } after) {
            FinishIfExhausted();
            return;
        }
        var delta = after.Y - _anchorY;
        if (Math.Abs(delta) < 0.5) {
            FinishIfExhausted();
            return;
        }
        _restoringAnchor = true;
        _scroll.Offset = new Vector(_scroll.Offset.X, _scroll.Offset.Y + delta);
        _restoringAnchor = false;
        FinishIfExhausted();
    }

    void FinishIfExhausted() {
        if (--_anchorPasses > 0) return;
        DropToggleAnchor();
    }

    void DropToggleAnchor() {
        if (_scroll is not null) _scroll.LayoutUpdated -= OnAnchorLayout;
        _anchorItem = null;
        _anchorPasses = 0;
        _anchorLaidOut = false;
    }

    void RealizeAnchor() {
        if (_anchorItem is null || ChatItems.Items is null) return;
        var index = 0;
        foreach (var item in ChatItems.Items) {
            if (ReferenceEquals(item, _anchorItem)) {
                ChatItems.ScrollIntoView(index);
                ChatItems.UpdateLayout();
                return;
            }
            index++;
        }
    }

    Button? ToolSummaryFor(object item) =>
        ChatItems.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("toolSummary") && ReferenceEquals(b.DataContext, item));

    static Button? ToolSummaryOf(object? source) {
        if (source is Button button && button.Classes.Contains("toolSummary")) return button;
        return source is Visual visual
            ? visual.GetVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("toolSummary"))
            : null;
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_restoringAnchor || _anchorItem is not null) return;
        if (sender is not ScrollViewer scroll) return;
        if (ListTextBoxOwnsFocus()) return;
        var atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - BottomTolerance;
        _followTail = _readerGesture ? atBottom : _followTail || atBottom;
        if (_followTail && !atBottom) scroll.ScrollToEnd();
    }

    static bool OriginatesFromTextBox(RoutedEventArgs e) =>
        e.Source is Visual visual && (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any());

    bool ListTextBoxOwnsFocus() {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box) return false;
        return box.GetVisualAncestors().OfType<ItemsControl>().Any(c => ReferenceEquals(c, ChatItems));
    }

    /// The composer wraps, so the rendered rows, not the newlines, say whether the caret has a row
    /// above or below it; the newline count stands in only before the box has a text presenter.
    (int Line, int Lines) ComposerCaretLine() {
        var text = ComposerInput.Text ?? "";
        var caret = Math.Clamp(ComposerInput.CaretIndex, 0, text.Length);
        var layout = ComposerInput.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.TextLayout;
        if (layout is { TextLines.Count: > 0 })
            return (layout.GetLineIndexFromCharacterIndex(caret, false), layout.TextLines.Count);
        return (text.AsSpan(0, caret).Count('\n'), text.AsSpan().Count('\n') + 1);
    }

    /// A bare Enter is always consumed — it sends when the composer can send, and otherwise does
    /// nothing, leaving the text and the hint that says why. Shift+Enter falls through to the
    /// TextBox's own newline. ↑/↓ recall sent prompts only from the first/last line, so inside a
    /// multi-line recall they stay the TextBox's own caret moves until the caret reaches an edge.
    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) {
            e.Handled = true;
            if (DataContext is ChatTabViewModel chat && ((ICommand)chat.InterruptCommand).CanExecute(null))
                chat.InterruptCommand.Execute().Subscribe();
            return;
        }
        if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.None) {
            if (DataContext is not ChatTabViewModel tab || ComposerInput.SelectionStart != ComposerInput.SelectionEnd) return;
            var (line, lines) = ComposerCaretLine();
            var recalled = e.Key == Key.Up ? line == 0 && tab.RecallOlder() : line == lines - 1 && tab.RecallNewer();
            if (!recalled) return;
            e.Handled = true;
            ComposerInput.CaretIndex = ComposerInput.Text?.Length ?? 0;
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatTabViewModel vm && ((ICommand)vm.SendCommand).CanExecute(null))
            vm.SendCommand.Execute().Subscribe();
    }

    public void FocusComposer() => ComposerInput.Focus();
}
