using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public sealed class InMemoryAppStateStore(AppState? initial = null, bool writesSucceed = true) : IAppStateStore {
    public AppState State { get; private set; } = initial ?? new AppState();

    public Task<AppState> LoadAsync() => Task.FromResult(State);

    public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
        if (writesSucceed) State = mutate(State);
        return Task.FromResult(writesSucceed);
    }
}
