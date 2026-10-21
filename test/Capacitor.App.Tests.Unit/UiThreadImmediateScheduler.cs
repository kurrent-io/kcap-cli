using System.Reactive.Concurrency;
using Avalonia.Threading;

namespace Capacitor.App.Tests.Unit;

/// Runs work inline on the UI thread and posts work from any other thread to it, as the app's
/// scheduler does. A plain immediate scheduler would run an emission from a service's pool thread
/// right there, mutating view models while the test reads them on the UI thread.
internal sealed class UiThreadImmediateScheduler(IScheduler ui) : LocalScheduler {
    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) =>
        Dispatcher.UIThread.CheckAccess()
            ? ImmediateScheduler.Instance.Schedule(state, dueTime, action)
            : ui.Schedule(state, dueTime, action);
}
