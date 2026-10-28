using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// The app's one UI scale, shared by the main window and Settings and kept in app state.
public sealed class UiScaleSetting {
    readonly IAppStateStore _store;
    readonly BehaviorSubject<double> _changes;

    UiScaleSetting(IAppStateStore store, double initial) {
        _store = store;
        _changes = new BehaviorSubject<double>(initial);
    }

    public static async Task<UiScaleSetting> LoadAsync(IAppStateStore store) =>
        new(store, UiScale.Normalize((await store.LoadAsync().ConfigureAwait(false)).UiScale));

    public double Current => _changes.Value;

    /// The current scale, then each change.
    public IObservable<double> Changes => _changes;

    /// A failed write keeps the scale for this run. Each write stores the scale current when it runs,
    /// so overlapping changes cannot leave an older one on disk.
    public Task SetAsync(double scale) {
        var next = UiScale.Normalize(scale);
        if (next == _changes.Value) return Task.CompletedTask;

        _changes.OnNext(next);
        return _store.UpdateAsync(state => state with { UiScale = _changes.Value });
    }
}
