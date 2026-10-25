using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

/// Rail preferences keyed by the signed-in user, with the user each profile last resolved to, so the
/// rail can open in that user's shape before the lane has said who is signed in.
public sealed class RailPreferencesStore(IAppStateStore store) {
    public static string UserKey(string serverUrl, string userId) => $"{serverUrl.TrimEnd('/')}|{userId}";
    public static string ProfileKey(string serverUrl, string profileName) => $"{serverUrl.TrimEnd('/')}|{profileName}";

    /// The set of the user this profile last resolved to, else the defaults.
    public async Task<RailPreferences> LoadAsync(string serverUrl, string profileName) {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return state.LastUserByProfile is { } users && users.TryGetValue(ProfileKey(serverUrl, profileName), out var userId)
            ? For(state, serverUrl, userId)
            : RailPreferences.Default;
    }

    /// Records that the profile resolved to this user and returns that user's set, else the defaults.
    public async Task<RailPreferences> BindUserAsync(string serverUrl, string profileName, string userId) {
        var key = ProfileKey(serverUrl, profileName);
        await store.UpdateAsync(state => state with {
            LastUserByProfile = new Dictionary<string, string>(state.LastUserByProfile ?? new Dictionary<string, string>(), StringComparer.Ordinal) { [key] = userId },
        }).ConfigureAwait(false);
        return For(await store.LoadAsync().ConfigureAwait(false), serverUrl, userId);
    }

    /// The user this profile last resolved to, if any.
    public async Task<string?> LastUserAsync(string serverUrl, string profileName) {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return state.LastUserByProfile is { } users && users.TryGetValue(ProfileKey(serverUrl, profileName), out var userId) ? userId : null;
    }

    public Task SaveAsync(string serverUrl, string userId, RailPreferences preferences) =>
        store.UpdateAsync(state => state with {
            RailPreferencesByUser = new Dictionary<string, RailPreferences>(state.RailPreferencesByUser ?? new Dictionary<string, RailPreferences>(), StringComparer.Ordinal) {
                [UserKey(serverUrl, userId)] = preferences,
            },
        });

    static RailPreferences For(AppState state, string serverUrl, string userId) =>
        state.RailPreferencesByUser is { } sets && sets.TryGetValue(UserKey(serverUrl, userId), out var prefs) ? prefs : RailPreferences.Default;
}
