using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail follows layout changes until the reader scrolls away, and resumes when they
/// return to the bottom. Gestures distinguish deliberate scrolling from layout adjustments.
public partial class ChatTabView : UserControl {
    const double BottomTolerance = 2;
    const double WheelStep = 50;
    const double SmoothHalfLifeSeconds = 0.032;

    readonly HashSet<MarkdownView> _selectedMarkdown = [];
    IDisposable? _sendSubscription;
    ScrollViewer? _scroll;
    TextPresenter? _composerPresenter;
    AttachmentDropPaste? _attachments;
    bool _followTail = true;
    /// Armed by a gesture inside the list and released once the dispatcher queue drains past
    /// layout, so it covers exactly the scroll changes that gesture produced — an expansion click
    /// or a wheel notch — and not the appends that land later.
    bool _readerGesture;
    int _listInputVersion;
    bool _thumbDragging;
    bool _dragSelecting;
    bool _edgeScrollQueued;
    Point _dragInScroll;
    MarkdownView? _dragView;
    /// A press is in flight. Follow-tail must not move the transcript under that pointer, or a
    /// link click is measured as a drag and dropped.
    IPointer? _primaryPointer;
    TopLevel? _pointerRoot;
    bool _tailDeferred;
    bool PrimaryDown => _primaryPointer is not null;
    double? _thumbFraction;
    double _smoothRemaining;
    bool _smoothFrameQueued;
    TimeSpan _lastSmoothFrame;
    /// Keep the toggled header at its viewport Y while the group's height settles.
    object? _anchorItem;
    Button? _anchorSummary;
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
        ChatItems.AddHandler(PointerMovedEvent, OnSelectionDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(PointerMovedEvent, OnSelectionMove, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(PointerReleasedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerReleasedEvent, OnSelectionDragEnded, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerWheelChangedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerWheelChangedEvent, OnChatWheelChanged, RoutingStrategies.Tunnel);
        ChatItems.AddHandler(KeyDownEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Bubble-only events: a tunnel registration for these never fires.
        ChatItems.AddHandler(ScrollGestureEvent, OnReaderGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragDeltaEvent, OnReaderGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragStartedEvent, OnChatThumbDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragDeltaEvent, OnChatThumbDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragCompletedEvent, OnChatThumbDrag, RoutingStrategies.Bubble, handledEventsToo: true);
        ComposerInput.TemplateApplied += (_, e) =>
            _composerPresenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        // The ScrollViewer is the list template's; it exists only once the list is first measured,
        // which for a surface built before its first layout is later than the first rows.
        ChatItems.TemplateApplied += (_, _) => {
            var next = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (ReferenceEquals(next, _scroll)) return;
            DropToggleAnchor();
            CancelSmoothScroll();
            _thumbDragging = false;
            _thumbFraction = null;
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
        ObserveSending();
        _pointerRoot = TopLevel.GetTopLevel(this);
        _pointerRoot?.AddHandler(PointerReleasedEvent, OnPrimaryReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _pointerRoot?.AddHandler(PointerCaptureLostEvent, OnPrimaryCaptureLost, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        if (_pointerRoot is Window window) window.Deactivated += OnWindowDeactivated;
        _attachments ??= AttachmentDropPaste.Attach(
            ComposerCard, ComposerInput, AttachButton,
            () => (DataContext as ChatTabViewModel)?.Attachments, TimeProvider.System);
    }

    /// The window reuses this view for every session it opens, so the reading position belongs to
    /// the session it was showing: the next one starts at the tail.
    protected override void OnDataContextChanged(EventArgs e) {
        DropToggleAnchor();
        CancelSmoothScroll();
        _thumbDragging = false;
        _thumbFraction = null;
        _readerGesture = false;
        _dragSelecting = false;
        _dragView = null;
        _selectedMarkdown.Clear();
        _primaryPointer = null;
        _tailDeferred = false;
        _followTail = true;
        base.OnDataContextChanged(e);
        ObserveSending();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        DropToggleAnchor();
        CancelSmoothScroll();
        _thumbDragging = false;
        _thumbFraction = null;
        _dragSelecting = false;
        _dragView = null;
        _sendSubscription?.Dispose();
        _sendSubscription = null;
        _primaryPointer = null;
        _tailDeferred = false;
        _pointerRoot?.RemoveHandler(PointerReleasedEvent, OnPrimaryReleased);
        _pointerRoot?.RemoveHandler(PointerCaptureLostEvent, OnPrimaryCaptureLost);
        if (_pointerRoot is Window window) window.Deactivated -= OnWindowDeactivated;
        _pointerRoot = null;
        _attachments?.Dispose();
        _attachments = null;
        base.OnDetachedFromVisualTree(e);
    }

    void ObserveSending() {
        _sendSubscription?.Dispose();
        _sendSubscription = DataContext is ChatTabViewModel vm
            ? Observable.FromEvent(h => vm.TurnSubmitted += h, h => vm.TurnSubmitted -= h)
                .Subscribe(_ => ResumeFollowing())
            : null;
    }

    void ResumeFollowing() {
        DropToggleAnchor();
        CancelSmoothScroll();
        _thumbDragging = false;
        _thumbFraction = null;
        _readerGesture = false;
        _dragSelecting = false;
        _dragView = null;
        _primaryPointer = null;
        _tailDeferred = false;
        foreach (var markdown in _selectedMarkdown.ToArray()) markdown.ClearSelection();
        _followTail = true;
        EnsureChatScroll();
        _scroll?.ScrollToEnd();
    }

    internal int ListInputVersion => _listInputVersion;
    internal bool IsSmoothScrolling => _smoothFrameQueued || Math.Abs(_smoothRemaining) > 0.5;
    internal bool CanPreserveReadingPosition => !_followTail && _anchorItem is null && _thumbFraction is null;

    void OnReaderGesture(object? sender, RoutedEventArgs e) {
        if (e is PointerPressedEventArgs pressed && _primaryPointer is null
            && pressed.GetCurrentPoint(ChatItems).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            _primaryPointer = pressed.Pointer;
        _listInputVersion++;
        // A TextBox in the list (Other…, free-text) is editing, not reading: arming follow-tail
        // would ScrollToEnd and recycle its virtualizing row, which drops the caret.
        if (OriginatesFromTextBox(e)) {
            CancelSmoothScroll();
            return;
        }
        if (e.RoutedEvent == PointerWheelChangedEvent || e.RoutedEvent == ScrollGestureEvent) DropToggleAnchor();
        if (e.RoutedEvent == PointerWheelChangedEvent || e.RoutedEvent == ScrollGestureEvent || e is KeyEventArgs) _thumbFraction = null;
        if (e.RoutedEvent == ScrollGestureEvent || e.RoutedEvent == PointerPressedEvent || e is KeyEventArgs) CancelSmoothScroll();
        // A press, or Enter or Space, inside a pending card is answering it — an option, Next, a step
        // chip — and the card then changes its own height in this layout pass. Read as the reader
        // scrolling up, that would stop following exactly when the next question lands below the
        // fold. Any other key stays the reader's: Page Up from a focused option pages the list.
        if (IsActivation(e) && OriginatesFromPendingCard(e)) return;
        if (e.RoutedEvent == PointerPressedEvent) CaptureToggleAnchor(e.Source);
        if (_readerGesture) return;
        _readerGesture = true;
        Dispatcher.UIThread.Post(() => {
            _readerGesture = false;
            RefreshSelectionFollowTail();
        }, DispatcherPriority.Background);
    }

    void OnPrimaryReleased(object? sender, PointerReleasedEventArgs e) {
        if (ReferenceEquals(e.Pointer, _primaryPointer) && e.InitialPressMouseButton == MouseButton.Left)
            CompletePrimaryPress();
    }

    void OnPrimaryCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (ReferenceEquals(e.Pointer, _primaryPointer)) CompletePrimaryPress();
    }

    void OnWindowDeactivated(object? sender, EventArgs e) => CompletePrimaryPress();

    void CompletePrimaryPress() {
        _primaryPointer = null;
        _dragSelecting = false;
        if (!_tailDeferred) return;
        _tailDeferred = false;
        var context = DataContext;
        Dispatcher.UIThread.Post(() => {
            if (PrimaryDown || !_followTail || _dragSelecting || _anchorItem is not null
                || _thumbFraction is not null || ListTextBoxOwnsFocus()
                || SelectionHeld() || !ReferenceEquals(context, DataContext) || _pointerRoot is null) return;
            _scroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    void OnChatWheelChanged(object? sender, PointerWheelEventArgs e) {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || OriginatesFromTextBox(e)) return;
        EnsureChatScroll();
        if (_scroll is null || DefersToInnerScroll(e)) return;
        var delta = -e.Delta.Y * WheelStep;
        if (Math.Abs(delta) < 0.01) return;
        e.Handled = true;
        StartSmoothScroll(delta);
    }

    void StartSmoothScroll(double delta) {
        if (_scroll is null) return;
        if (Math.Sign(delta) != Math.Sign(_smoothRemaining)) _smoothRemaining = 0;
        _smoothRemaining += delta;
        StepSmoothScroll(0.28);
        QueueSmoothFrame();
    }

    void QueueSmoothFrame() {
        if (_smoothFrameQueued || Math.Abs(_smoothRemaining) <= 0.5) return;
        if (TopLevel.GetTopLevel(this) is not { } top) {
            StepSmoothScroll(1);
            return;
        }
        _smoothFrameQueued = true;
        RequestSmoothFrame(top, OnSmoothScrollFrame);
    }

    /// The render loop stamps frames with wall-clock time and ticks on its own timer, so a test
    /// that asserts on the curve supplies the frames and their timestamps itself.
    internal Action<TopLevel, Action<TimeSpan>> RequestSmoothFrame { get; set; } =
        static (top, frame) => top.RequestAnimationFrame(frame);

    void OnSmoothScrollFrame(TimeSpan timestamp) {
        _smoothFrameQueued = false;
        if (_scroll is null || Math.Abs(_smoothRemaining) <= 0.5) {
            _smoothRemaining = 0;
            _lastSmoothFrame = default;
            return;
        }
        var dt = _lastSmoothFrame == default ? 1.0 / 120.0 : Math.Clamp((timestamp - _lastSmoothFrame).TotalSeconds, 1.0 / 240.0, 0.05);
        _lastSmoothFrame = timestamp;
        var fraction = Math.Abs(_smoothRemaining) < 1.5 ? 1.0 : 1.0 - Math.Pow(0.5, dt / SmoothHalfLifeSeconds);
        StepSmoothScroll(fraction);
        if (Math.Abs(_smoothRemaining) > 0.5) QueueSmoothFrame();
        else _lastSmoothFrame = default;
    }

    void StepSmoothScroll(double fraction) {
        if (_scroll is null) return;
        var step = fraction >= 1.0 ? _smoothRemaining : _smoothRemaining * fraction;
        _smoothRemaining -= step;
        if (Math.Abs(_smoothRemaining) <= 0.5) {
            step += _smoothRemaining;
            _smoothRemaining = 0;
        }
        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var next = Math.Clamp(_scroll.Offset.Y + step, 0, max);
        var moved = next - _scroll.Offset.Y;
        if (Math.Abs(moved) < 0.01 && Math.Sign(step) == Math.Sign(_smoothRemaining)) {
            _smoothRemaining = 0;
            _lastSmoothFrame = default;
        }
        var wasGesture = _readerGesture;
        _readerGesture = true;
        _scroll.Offset = new Vector(_scroll.Offset.X, next);
        if (!wasGesture) Dispatcher.UIThread.Post(() => _readerGesture = false, DispatcherPriority.Background);
    }

    void CancelSmoothScroll() {
        _smoothRemaining = 0;
        _lastSmoothFrame = default;
    }

    // A markdown row hosts a ScrollViewer whose scrolling is disabled. Only an inner viewer
    // that can still move in this direction owns the notch.
    bool DefersToInnerScroll(PointerWheelEventArgs e) {
        if (e.Source is not Visual visual) return false;
        foreach (var inner in visual.GetSelfAndVisualAncestors().OfType<ScrollViewer>()) {
            if (ReferenceEquals(inner, _scroll)) return false;
            if (inner.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var range = inner.Extent.Height - inner.Viewport.Height;
            if (range <= 1) continue;
            var towardStart = e.Delta.Y > 0;
            if (towardStart ? inner.Offset.Y <= 0.5 : inner.Offset.Y >= range - 0.5) continue;
            return true;
        }
        return false;
    }

    void OnChatThumbDrag(object? sender, RoutedEventArgs e) {
        if (_scroll is null || e.Source is not Thumb thumb
            || !ReferenceEquals(thumb.FindAncestorOfType<ScrollBar>()?.TemplatedParent, _scroll)) return;
        if (e.RoutedEvent == Thumb.DragCompletedEvent) {
            _thumbDragging = false;
            Dispatcher.UIThread.Post(() => {
                if (!_thumbDragging) _thumbFraction = null;
            }, DispatcherPriority.Background);
            return;
        }
        if (e.RoutedEvent == Thumb.DragStartedEvent) {
            _thumbDragging = true;
            CancelSmoothScroll();
            DropToggleAnchor();
        }
        var range = _scroll.Extent.Height - _scroll.Viewport.Height;
        if (range > 0) _thumbFraction = Math.Clamp(_scroll.Offset.Y / range, 0, 1);
    }

    async void OnCopyLinkClick(object? sender, RoutedEventArgs e) {
        if (sender is not Control { Tag: string { Length: > 0 } url } control) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try {
            await clipboard.SetTextAsync(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: copy link failed: {ex.Message}");
            return;
        }
        ToolTip.SetTip(control, "Copied");
        ToolTip.SetIsOpen(control, true);
    }

    // Click is raised before the command flips the group, so a keyboard toggle (no pointer press
    // ahead of it) can still take its anchor here.
    void OnToolSummaryClick(object? sender, RoutedEventArgs e) {
        _followTail = false;
        CancelSmoothScroll();
        if (_anchorItem is null) CaptureToggleAnchor(sender);
        ArmToggleRestore();
    }

    void CaptureToggleAnchor(object? source) {
        EnsureChatScroll();
        if (_scroll is null || ToolSummaryOf(source) is not { } summary) return;
        if (summary.TranslatePoint(new Point(0, 0), _scroll) is not { } point) return;
        _anchorItem = summary.DataContext;
        _anchorSummary = summary;
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
            DropToggleAnchor();
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
        _anchorSummary = null;
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

    Button? ToolSummaryFor(object item) {
        if (_anchorSummary is { } cached && ReferenceEquals(cached.DataContext, item)
            && cached.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, ChatItems))) return cached;
        _anchorSummary = ChatItems.ContainerFromItem(item)?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("toolSummary") && ReferenceEquals(button.DataContext, item));
        return _anchorSummary;
    }

    static Button? ToolSummaryOf(object? source) {
        if (source is Button button && button.Classes.Contains("toolSummary")) return button;
        return source is Visual visual
            ? visual.GetVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("toolSummary"))
            : null;
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (_restoringAnchor || _anchorItem is not null) return;
        if (sender is not ScrollViewer scroll) return;
        // A dragged thumb owns its position even when discovering row heights changes the range.
        if (_thumbFraction is { } fraction) {
            var range = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var target = fraction * range;
            _followTail = range - target <= BottomTolerance;
            if (Math.Abs(scroll.Offset.Y - target) > 0.5) scroll.Offset = new Vector(scroll.Offset.X, target);
            return;
        }
        if (ListTextBoxOwnsFocus()) return;
        if (IsSmoothScrolling && _smoothRemaining < -0.5) {
            _followTail = false;
            return;
        }
        var atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - BottomTolerance;
        // Returning to the estimated end can measure new rows and grow the extent in the
        // same layout pass. That still counts as the reader returning to the bottom.
        var reachedEstimatedEnd = e.OffsetDelta.Y > 0 && e.ExtentDelta.Y > 0
            && ChatItems.ItemsPanelRoot is ChatVirtualizingPanel { MeasuredAtEstimatedEnd: true };
        var extentOnlyWhilePressed = PrimaryDown && _followTail
            && e.ExtentDelta.Y > 0 && Math.Abs(e.OffsetDelta.Y) <= 0.5;
        _followTail = extentOnlyWhilePressed || (_readerGesture ? atBottom || reachedEstimatedEnd : _followTail || atBottom || reachedEstimatedEnd);
        if (SelectionHeld()) _followTail = false;
        else if (_followTail && !atBottom) {
            if (PrimaryDown) _tailDeferred = true;
            else scroll.ScrollToEnd();
        }
    }

    void OnSelectionDrag(object? sender, PointerEventArgs e) {
        EnsureChatScroll();
        if (_scroll is null) return;
        if (!e.GetCurrentPoint(ChatItems).Properties.IsLeftButtonPressed || OriginatesFromTextBox(e)) {
            _dragSelecting = false;
            return;
        }
        if (e.Source is not Visual visual) return;
        var view = visual.GetSelfAndVisualAncestors().OfType<MarkdownView>().FirstOrDefault();
        if (!_dragSelecting) {
            if (view is not { IsSelecting: true }) return;
            _dragView = view;
            _dragSelecting = true;
        } else if (view is { IsSelecting: true }) {
            _dragView = view;
        }
        if (_dragView is { HasSelection: true }) _followTail = false;
        _dragInScroll = e.GetPosition(_scroll);
        QueueEdgeScroll();
    }

    void OnSelectionDragEnded(object? sender, RoutedEventArgs e) => _dragSelecting = false;

    void QueueEdgeScroll() {
        if (_edgeScrollQueued || !_dragSelecting || _scroll is null) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _edgeScrollQueued = true;
        top.RequestAnimationFrame(_ => {
            _edgeScrollQueued = false;
            if (_dragSelecting) StepEdgeScroll();
        });
    }

    void StepEdgeScroll() {
        if (_scroll is null || !_dragSelecting) return;
        const double edge = 28;
        const double step = 18;
        var y = _dragInScroll.Y;
        var height = _scroll.Viewport.Height;
        var delta = y < edge ? -step : y > height - edge ? step : 0.0;
        if (Math.Abs(delta) < 0.01) return;
        var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        var next = Math.Clamp(_scroll.Offset.Y + delta, 0, max);
        if (Math.Abs(next - _scroll.Offset.Y) > 0.5) {
            var wasGesture = _readerGesture;
            _readerGesture = true;
            _scroll.Offset = new Vector(_scroll.Offset.X, next);
            if (!wasGesture) Dispatcher.UIThread.Post(() => _readerGesture = false, DispatcherPriority.Background);
        }
        if (_dragView?.TranslatePoint(default, _scroll) is { } origin) {
            _dragView.ExtendSelection(new Point(_dragInScroll.X - origin.X, _dragInScroll.Y - origin.Y));
            if (_dragView.HasSelection) _followTail = false;
        }
        QueueEdgeScroll();
    }

    void OnSelectionMove(object? sender, PointerEventArgs e) {
        if (!e.GetCurrentPoint(ChatItems).Properties.IsLeftButtonPressed || !OriginatesFromMarkdown(e)) return;
        RefreshSelectionFollowTail();
    }

    void RefreshSelectionFollowTail() {
        if (SelectionHeld()) {
            _followTail = false;
            return;
        }
        if (_scroll is null || _anchorItem is not null || _thumbFraction is not null
            || IsSmoothScrolling || ListTextBoxOwnsFocus()) return;
        if (_scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - BottomTolerance)
            _followTail = true;
    }

    internal void UpdateSelection(MarkdownView view) {
        if (view.HasSelection) {
            _selectedMarkdown.Add(view);
            if (view.IsEffectivelyVisible) _followTail = false;
        } else RemoveSelection(view);
    }

    internal void RemoveSelection(MarkdownView view) {
        if (_selectedMarkdown.Remove(view)) RefreshSelectionFollowTail();
    }

    bool SelectionHeld() => _selectedMarkdown.Any(static view => view.IsEffectivelyVisible);

    static bool OriginatesFromMarkdown(RoutedEventArgs e) =>
        e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<MarkdownView>().Any();

    static bool OriginatesFromTextBox(RoutedEventArgs e) =>
        e.Source is Visual visual && (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any());

    static bool IsActivation(RoutedEventArgs e) =>
        e.RoutedEvent == PointerPressedEvent || e.RoutedEvent == PointerReleasedEvent
        || e is KeyEventArgs { Key: Key.Enter or Key.Space };

    static bool OriginatesFromPendingCard(RoutedEventArgs e) =>
        e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<StyledElement>().Any(s => s.Classes.Contains("pendingCard"));

    bool ListTextBoxOwnsFocus() {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box) return false;
        return box.GetVisualAncestors().OfType<ItemsControl>().Any(c => ReferenceEquals(c, ChatItems));
    }

    /// The composer wraps, so the rendered rows, not the newlines, say whether the caret has a row
    /// above or below it; the newline count stands in only before the box has a text presenter.
    (int Line, int Lines) ComposerCaretLine() {
        var text = ComposerInput.Text ?? "";
        var caret = Math.Clamp(ComposerInput.CaretIndex, 0, text.Length);
        var layout = _composerPresenter?.TextLayout;
        if (layout is { TextLines.Count: > 0 })
            return (layout.GetLineIndexFromCharacterIndex(caret, false), layout.TextLines.Count);
        return (text.AsSpan(0, caret).Count('\n'), text.AsSpan().Count('\n') + 1);
    }

    /// A bare Enter is always consumed — it sends when the composer can send, and otherwise does
    /// nothing, leaving the text and the hint that says why. Shift+Enter falls through to the
    /// TextBox's own newline. ↑/↓ recall sent prompts only from the first/last line, so inside a
    /// multi-line recall they stay the TextBox's own caret moves until the caret reaches an edge.
    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
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

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || OriginatesFromPendingCard(e)) return;
        // A composer Escape stops here even with nothing to interrupt, so an ancestor's Escape
        // binding never fires while the user is typing.
        if (TryInterrupt() || e.Source is Visual source && (source == ComposerInput || ComposerInput.IsVisualAncestorOf(source)))
            e.Handled = true;
    }

    bool TryInterrupt() {
        if (DataContext is not ChatTabViewModel chat || !chat.CanInterruptWithEscape
            || !((ICommand)chat.InterruptCommand).CanExecute(null)) return false;
        chat.InterruptCommand.Execute().Subscribe();
        return true;
    }

    public void FocusComposer() => ComposerInput.Focus();
}
