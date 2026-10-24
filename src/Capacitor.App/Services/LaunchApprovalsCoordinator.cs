using Avalonia.Controls;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

public sealed class LaunchApprovalsCoordinator(ActivityViewModel activity) : IDisposable {
    LaunchApprovalsWindow? _window;
    bool _disposed;

    public LaunchApprovalsWindow? Window => _window;

    public void Show() {
        if (_disposed) return;
        if (_window is null) {
            var window = new LaunchApprovalsWindow { DataContext = activity };
            window.Closed += (_, _) => _window = null;
            _window = window;
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    public void Dispose() {
        _disposed = true;
        _window?.Close();
        _window = null;
    }
}
