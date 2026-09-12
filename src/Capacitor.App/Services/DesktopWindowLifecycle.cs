using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Capacitor.App.Services;

/// Install before the first window is shown. Minimized windows remain visible to Avalonia and
/// keep the Dock entry; hiding or closing the last window leaves the app in the tray.
public sealed class DesktopWindowLifecycle : IDisposable {
    readonly IActivatableLifetime? _activation;
    readonly Func<Action?> _showMainWindow;
    readonly Action<bool> _setDockVisibility;
    readonly IDisposable _visibilitySubscription;
    readonly HashSet<Window> _visibleWindows = [];
    bool? _dockVisible;
    bool _disposed;

    public DesktopWindowLifecycle(IActivatableLifetime? activation, Func<Action?> showMainWindow, Action<bool> setDockVisibility) {
        _activation = activation;
        _showMainWindow = showMainWindow;
        _setDockVisibility = setDockVisibility;
        // IsVisible changes before the native Show call, so the Dock policy is restored before
        // AppKit activates the window. Opened is too late for that ordering.
        _visibilitySubscription = Window.IsVisibleProperty.Changed.AddClassHandler<Window>((window, _) => OnVisibilityChanged(window));
        if (_activation is not null) _activation.Activated += OnActivated;
    }

    void OnActivated(object? sender, ActivatedEventArgs e) {
        if (e.Kind == ActivationKind.Reopen) _showMainWindow()?.Invoke();
    }

    void OnVisibilityChanged(Window window) {
        if (window.IsVisible) {
            if (!_visibleWindows.Add(window)) return;
            window.Closed += OnClosed;
        } else if (!Remove(window)) {
            return;
        }

        UpdateDockVisibility();
    }

    void OnClosed(object? sender, EventArgs e) {
        if (sender is Window window && Remove(window)) UpdateDockVisibility();
    }

    bool Remove(Window window) {
        if (!_visibleWindows.Remove(window)) return false;
        window.Closed -= OnClosed;
        return true;
    }

    void UpdateDockVisibility() {
        var visible = _visibleWindows.Count != 0;
        if (_dockVisible == visible) return;
        _dockVisible = visible;
        _setDockVisibility(visible);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_activation is not null) _activation.Activated -= OnActivated;
        _visibilitySubscription.Dispose();
        foreach (var window in _visibleWindows) window.Closed -= OnClosed;
        _visibleWindows.Clear();
    }
}
