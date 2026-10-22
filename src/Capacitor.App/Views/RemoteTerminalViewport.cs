using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public sealed class RemoteTerminalViewport : ScrollViewer {
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    bool _followTail = true;
    WheelStepAccumulator _wheel;

    public RemoteTerminalViewport() {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        AddHandler(PointerWheelChangedEvent, OnTerminalWheel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => {
            var remoteKey = e.Key is Key.Up or Key.Down or Key.Enter or Key.Tab or Key.Escape
                || (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control);
            if (remoteKey && Content is RemoteTerminalControl { IsKeyboardFocusWithin: true }) ShowTail();
        }, RoutingStrategies.Tunnel);
        ScrollChanged += OnScrollChanged;
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (Content is not RemoteTerminalControl terminal) return;
        var end = Math.Max(0, Extent.Height - Viewport.Height);
        if (_followTail && (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0))
            Offset = new Vector(Offset.X, end);
        _followTail = Offset.Y >= end - 1;
        terminal.SetViewport(Viewport);
    }

    public void ShowTail() {
        if (Content is RemoteTerminalControl terminal) terminal.Model?.EnsureCaretIsVisible();
        Offset = new Vector(Offset.X, Math.Max(0, Extent.Height - Viewport.Height));
        _followTail = true;
    }

    void OnTerminalWheel(object? sender, PointerWheelEventArgs e) {
        if (Content is not RemoteTerminalControl terminal || terminal.Model is not { } model) return;
        if (!double.IsFinite(e.Delta.Y) || !double.IsFinite(e.Delta.X)) { e.Handled = true; return; }
        // Mouse-tracking apps own scrolling in either buffer. Their intermediate redraws are not history.
        if (model.IsMouseModeActive) {
            _wheel = default;
            terminal.SendWheel(e.Delta.Y, e.GetPosition(terminal));
            e.Handled = true;
            return;
        }
        var end = Math.Max(0, Extent.Height - Viewport.Height);
        var rows = Math.Clamp(-e.Delta.Y * 3, -9, 9);
        if ((rows < 0 && Offset.Y > 0) || (rows > 0 && model.ScrollOffset == model.MaxScrollback)) {
            Offset = new Vector(Offset.X, Math.Clamp(Offset.Y + rows * terminal.CellHeight, 0, end));
            _wheel = default;
        } else {
            var lines = _wheel.Take(-e.Delta.Y * 3, 9);
            if (lines != 0) model.ScrollLines(lines);
        }
        if (e.Delta.X != 0)
            Offset = new Vector(Math.Clamp(Offset.X - Math.Clamp(e.Delta.X * 3, -9, 9) * terminal.CellHeight,
                0, Math.Max(0, Extent.Width - Viewport.Width)), Offset.Y);
        e.Handled = true;
    }
}
