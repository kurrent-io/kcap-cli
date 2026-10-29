namespace Capacitor.App.Services.Update;

/// The user's beta-updates choice, kept in app state. Null means no choice has been made.
public sealed class BetaUpdatesSetting {
    readonly IAppStateStore _store;
    readonly Lock _lock = new();
    bool? _choice;

    BetaUpdatesSetting(IAppStateStore store, bool? choice) {
        _store = store;
        _choice = choice;
    }

    public static async Task<BetaUpdatesSetting> LoadAsync(IAppStateStore store) =>
        new(store, (await store.LoadAsync().ConfigureAwait(false)).BetaUpdates);

    public bool? Choice {
        get {
            lock (_lock) return _choice;
        }
    }

    /// A failed write keeps the choice for this run. Each write stores the choice current when it
    /// runs, so overlapping changes cannot leave an older one on disk.
    public Task SetAsync(bool enabled) {
        lock (_lock) _choice = enabled;
        return _store.UpdateAsync(state => state with { BetaUpdates = Choice });
    }
}
