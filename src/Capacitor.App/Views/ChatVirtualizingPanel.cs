using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// <summary>
/// Chat rows have very different heights. Keep each measured height when its container is
/// recycled, rather than estimating the whole history from the rows currently on screen.
/// Unknown rows use a fixed estimate; discovering one never resizes other unknown rows.
/// </summary>
public sealed class ChatVirtualizingPanel : VirtualizingPanel {
    sealed record Row(object Item, Control Container, object? RecycleKey, bool OwnContainer);
    const double EstimatedHeight = 80;
    const double KeepAliveOverscan = 240;
    readonly Dictionary<object, double> _measured = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, Row> _realized = [];
    readonly Dictionary<object, Stack<Control>> _pool = [];
    double[] _starts = [0];
    Rect _viewport;
    double _lastWidth = double.NaN;
    int _first = -1;
    int _last = -1;

    internal bool MeasuredAtEstimatedEnd { get; private set; }

    public ChatVirtualizingPanel() => EffectiveViewportChanged += OnViewportChanged;

    void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e) {
        if (_viewport == e.EffectiveViewport) return;
        var old = _viewport;
        _viewport = e.EffectiveViewport;
        if (old.Size != _viewport.Size || _first < 0 || _last >= Items.Count
            || Math.Max(0, _viewport.Top) < _starts[_first]
            || Math.Min(_starts[^1], _viewport.Bottom) > _starts[_last + 1]) InvalidateMeasure();
    }

    void RebuildStarts() {
        _starts = new double[Items.Count + 1];
        for (var i = 0; i < Items.Count; i++)
            _starts[i + 1] = _starts[i] + (Items[i] is { } item ? _measured.GetValueOrDefault(item, EstimatedHeight) : EstimatedHeight);
    }

    int IndexAt(double y) {
        var index = Array.BinarySearch(_starts, y);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, Items.Count - 1));
    }

    protected override Size MeasureOverride(Size availableSize) {
        var scroll = this.FindAncestorOfType<ScrollViewer>();
        var view = this.FindAncestorOfType<ChatTabView>();
        MeasuredAtEstimatedEnd = scroll is not null && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 2;
        if (Items.Count == 0) { _first = _last = -1; return default; }
        if (_starts.Length != Items.Count + 1) RebuildStarts();
        var anchor = IndexAt(Math.Max(0, _viewport.Top));
        var buffered = _first >= 0 && _last < Items.Count
            && Math.Max(0, _viewport.Top) >= _starts[_first]
            && Math.Min(_starts[^1], _viewport.Bottom) <= _starts[_last + 1];
        var first = buffered ? _first : IndexAt(Math.Max(0, _viewport.Top));
        var end = Math.Max(_viewport.Bottom, _viewport.Top + 1);
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var scrollAnchor = this.FindAncestorOfType<IScrollAnchorProvider>()?.CurrentAnchor;
        if (availableSize.Width != _lastWidth && scrollAnchor is not null && scroll is not null)
            PreserveResizeAnchor(scroll, scrollAnchor);
        _lastWidth = availableSize.Width;
        var overscan = view?.IsSmoothScrolling == true ? KeepAliveOverscan : 0;
        // Keep the presenter's anchor until arrange has compensated for any newly measured rows.
        foreach (var entry in _realized.ToArray())
            if ((entry.Key < first || entry.Key > anchor && _starts[entry.Key] >= end) && !KeepAlive(entry.Value, focused, scrollAnchor, overscan))
                Unrealize(entry.Key);
        var last = first;
        var width = 0.0;
        var adjustment = 0.0;
        var previousStart = _starts[first];
        for (var i = first; i < Items.Count && (i <= anchor || previousStart + adjustment < end); i++) {
            var nextStart = _starts[i + 1];
            var oldHeight = nextStart - previousStart;
            _starts[i] = previousStart + adjustment;
            previousStart = nextStart;
            var row = Realize(i);
            row.Container.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var height = row.Container.DesiredSize.Height;
            adjustment += height - oldHeight;
            _measured[row.Item] = height;
            width = Math.Max(width, row.Container.DesiredSize.Width);
            last = i;
        }
        if (adjustment != 0)
            for (var i = last + 1; i < _starts.Length; i++) _starts[i] += adjustment;
        _first = first;
        _last = last;
        foreach (var entry in _realized.ToArray()) {
            if (entry.Key >= first && entry.Key <= last) continue;
            if (!KeepAlive(entry.Value, focused, scrollAnchor, overscan)) { Unrealize(entry.Key); continue; }
            // A warm row above the viewport keeps its stored height. Remeasuring it shifts every
            // start below it, and the presenter follows by moving the reader's offset.
            var pinned = entry.Value.OwnContainer || ReferenceEquals(entry.Value.Container, scrollAnchor)
                || focused is not null && (ReferenceEquals(focused, entry.Value.Container) || entry.Value.Container.IsVisualAncestorOf(focused));
            if (entry.Key < first && !pinned) continue;
            entry.Value.Container.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var height = entry.Value.Container.DesiredSize.Height;
            var delta = height - (_starts[entry.Key + 1] - _starts[entry.Key]);
            _measured[entry.Value.Item] = height;
            if (delta != 0)
                for (var i = entry.Key + 1; i < _starts.Length; i++) _starts[i] += delta;
        }
        return new Size(width, _starts[^1]);
    }

    void PreserveResizeAnchor(ScrollViewer scroll, Control anchor) {
        var view = this.FindAncestorOfType<ChatTabView>();
        if (view?.CanPreserveReadingPosition != true || anchor.TranslatePoint(default, scroll) is not { } before) return;
        var item = anchor.DataContext;
        var inputVersion = view.ListInputVersion;
        // Child layout can finish before the presenter gets an arrange pass to track its anchor.
        Dispatcher.UIThread.Post(() => {
            if (!view.CanPreserveReadingPosition || view.ListInputVersion != inputVersion
                || !ReferenceEquals(anchor.DataContext, item) || !this.IsVisualAncestorOf(anchor)
                || TopLevel.GetTopLevel(this) is null || anchor.TranslatePoint(default, scroll) is not { } after) return;
            var delta = after.Y - before.Y;
            if (Math.Abs(delta) > 0.5) scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + delta);
        }, DispatcherPriority.Render);
    }

    bool KeepAlive(Row row, Visual? focused, Control? scrollAnchor, double overscan) => row.OwnContainer
        || row.Container.Bounds.Intersects(overscan > 0 ? _viewport.Inflate(new Thickness(0, overscan)) : _viewport)
        || ReferenceEquals(row.Container, scrollAnchor)
        || focused is not null && (ReferenceEquals(focused, row.Container) || row.Container.IsVisualAncestorOf(focused));

    protected override Size ArrangeOverride(Size finalSize) {
        var anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
        foreach (var entry in _realized) {
            var container = entry.Value.Container;
            container.Arrange(new Rect(0, _starts[entry.Key], finalSize.Width, container.DesiredSize.Height));
            if (container.IsVisible && container.Bounds.Intersects(_viewport)) anchors?.RegisterAnchorCandidate(container);
        }
        return finalSize;
    }

    Row Realize(int index) {
        if (_realized.TryGetValue(index, out var existing)) return existing;
        var generator = ItemContainerGenerator!;
        var item = Items[index]!;
        var needsContainer = generator.NeedsContainer(item, index, out var key);
        Control? container = null;
        var pooled = needsContainer && key is not null && _pool.TryGetValue(key, out var pool) && pool.TryPop(out container);
        if (!pooled) container = needsContainer ? generator.CreateContainer(item, index, key) : (Control)item;
        container!.IsVisible = true;
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        var row = new Row(item, container, key, !needsContainer);
        _realized.Add(index, row);
        return row;
    }

    void Unrealize(int index) {
        if (!_realized.Remove(index, out var row)) return;
        this.FindAncestorOfType<IScrollAnchorProvider>()?.UnregisterAnchorCandidate(row.Container);
        if (row.OwnContainer) {
            RemoveInternalChild(row.Container);
            return;
        }
        ItemContainerGenerator!.ClearItemContainer(row.Container);
        if (row.RecycleKey is { } key) {
            row.Container.IsVisible = false;
            if (!_pool.TryGetValue(key, out var pool)) _pool.Add(key, pool = new Stack<Control>());
            pool.Push(row.Container);
        }
        RemoveInternalChild(row.Container);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e) {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewStartingIndex != _starts.Length - 1) {
            if (e.Action == NotifyCollectionChangedAction.Reset) {
                foreach (var index in _realized.Keys.ToArray()) Unrealize(index);
                _measured.Clear();
            } else {
                var indices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < items.Count; i++)
                    if (items[i] is { } item) indices[item] = i;
                var retained = new Dictionary<int, Row>();
                foreach (var entry in _realized.ToArray()) {
                    if (!indices.TryGetValue(entry.Value.Item, out var index)) Unrealize(entry.Key);
                    else {
                        retained.Add(index, entry.Value);
                        if (index != entry.Key) ItemContainerGenerator!.ItemContainerIndexChanged(entry.Value.Container, entry.Key, index);
                    }
                }
                _realized.Clear();
                foreach (var entry in retained) _realized.Add(entry.Key, entry.Value);
                foreach (var item in _measured.Keys.Where(item => !indices.ContainsKey(item)).ToArray()) _measured.Remove(item);
            }
            _first = _last = -1;
        }
        RebuildStarts();
        InvalidateMeasure();
    }

    protected override Control? ScrollIntoView(int index) {
        if (index < 0 || index >= Items.Count) return null;
        if (_starts.Length != Items.Count + 1) RebuildStarts();
        var row = Realize(index);
        InvalidateMeasure();
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroll) {
            var delta = _starts[index] - _viewport.Top;
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + delta);
        }
        return row.Container;
    }

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index)?.Container;
    protected override int IndexFromContainer(Control container) {
        foreach (var entry in _realized)
            if (ReferenceEquals(entry.Value.Container, container)) return entry.Key;
        return -1;
    }
    protected override IEnumerable<Control> GetRealizedContainers() => _realized.Values.Select(row => row.Container);
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) {
        if (Items.Count == 0 || from is null && direction is not NavigationDirection.First and not NavigationDirection.Last) return null;
        // Directional navigation starts at the focused control inside the row, not the row.
        var index = -1;
        for (var visual = from as Visual; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            if (visual is Control control && IndexFromContainer(control) is var found and >= 0) { index = found; break; }
        int? next = direction switch {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Up or NavigationDirection.Previous => index - 1,
            NavigationDirection.Down or NavigationDirection.Next => index + 1,
            NavigationDirection.Left or NavigationDirection.Right => index,
            _ => null,
        };
        if (next is not { } target) return null;
        if (target == index) return from;
        if (wrap) target = (target + Items.Count) % Items.Count;
        return ScrollIntoView(target);
    }
}
