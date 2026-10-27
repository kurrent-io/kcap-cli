using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Tests.Unit;

/// A docs figure's window and everything built for it, torn down newest first so a scene that
/// throws halfway releases exactly what it had built. Register what is built before building the
/// next thing.
sealed class DocsShotScene : IAsyncDisposable {
    readonly Stack<Func<Task>> _teardown = new();
    Window? _window;

    public Window Window => _window ?? throw new InvalidOperationException("The scene was never given a window");

    public T Own<T>(T disposable) where T : IDisposable {
        _teardown.Push(() => { disposable.Dispose(); return Task.CompletedTask; });
        return disposable;
    }

    public void Defer(Func<Task> teardown) => _teardown.Push(teardown);

    /// Closes the window ahead of everything registered before it.
    public TWindow Present<TWindow>(TWindow window) where TWindow : Window {
        _window = window;
        Defer(() => { window.Close(); return Task.CompletedTask; });
        return window;
    }

    public async ValueTask DisposeAsync() {
        while (_teardown.TryPop(out var step)) {
            await step();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
