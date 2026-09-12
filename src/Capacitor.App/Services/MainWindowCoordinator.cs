using Avalonia.Controls;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

/// Retains the main window across hide-to-tray cycles. Avalonia cannot show a window after a
/// real close, so only that path discards the instance.
/// <param name="releaseWorkspace">
/// Releases the workspace on hide and close: an invisible terminal must not keep clamping the
/// PTY for other viewers. The callback must tolerate a workspace that has already been released.
/// </param>
public sealed class MainWindowCoordinator(Func<MainWindow> windowFactory, Action<MainWindow>? releaseWorkspace = null) {
    MainWindow? _window;

    /// Prevents reopening and lets window teardown complete during deferred shutdown.
    public bool QuitInProgress { get; set; }

    /// The retained window, whether visible or hidden; null after a real close.
    public MainWindow? Window => _window;

    public void ShowMainWindow() {
        if (QuitInProgress) return;

        if (_window is null) {
            var window = windowFactory();
            window.CloseInterceptor = OnWindowClosing;
            window.Closed += (_, _) => {
                // A late close must release its own workspace without discarding a newer window.
                releaseWorkspace?.Invoke(window);
                if (ReferenceEquals(_window, window)) _window = null;
            };
            _window = window;
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    /// Returns true when the close must be cancelled because the window was hidden instead.
    public bool OnWindowClosing() {
        if (QuitInProgress) return false;

        if (_window is not null) releaseWorkspace?.Invoke(_window);
        _window?.Hide();
        return true;
    }
}
