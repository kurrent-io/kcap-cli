using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// App-owned UX state only — nothing lifecycle-safety-bearing lives here. Persisted
/// under the app's ConfigRoot as `app-state.json`; the CLI's own fixed-namespace
/// marker is the source of truth for anything safety-bearing.
public sealed record AppState(
    bool ShimOffered = false,
    bool ShimDenied = false,
    bool ConsentQuarantineAcked = false,
    // Absolute repo path -> vendor token. "" is the reserved key for the not-yet-in-a-repository
    // target (HomeViewModel.ScratchRepoPath) — a stored preference only, since the daemon does
    // not accept a repo-less launch. Absent key = never chosen here; the caller picks its own
    // default rather than inheriting another repository's choice.
    IReadOnlyDictionary<string, string>? HarnessByRepo = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    int? WindowX = null,
    int? WindowY = null,
    bool WindowMaximized = false,
    // Rail shape per signed-in user, keyed "{server}|{userId}"; and the user each profile
    // ("{server}|{profileName}") last resolved to, so startup opens in that user's shape.
    IReadOnlyDictionary<string, ViewModels.RailPreferences>? RailPreferencesByUser = null,
    IReadOnlyDictionary<string, string>? LastUserByProfile = null,
    double? UiScale = null,
    double? RailWidth = null,
    bool RailCollapsed = false,
    double? ContextPaneWidth = null,
    bool ContextPaneCollapsed = false,
    // When setup last sent the user to kcap.ai's sign-up; cleared once a sign-in commits.
    DateTimeOffset? WebSignUpOpenedAt = null,
    bool WindowsServiceDeclined = false,
    // Null follows the installed version: a beta install takes betas, a stable one does not.
    bool? BetaUpdates = null,
    // "host/vendor:repo" -> account id, written by AccountMemory.
    IReadOnlyDictionary<string, string>? AccountByRepo = null,
    // Vendor id -> whether kcap's tools were last installed for it from Settings. Nothing on disk
    // says so, unlike hooks, so this is the only record of that answer.
    IReadOnlyDictionary<string, bool>? HarnessToolsByVendor = null,
    // Absolute repo path -> ClaudePermissionModes token, keyed like HarnessByRepo.
    IReadOnlyDictionary<string, string>? PermissionModeByRepo = null,
    // A setup attempt is unverified until its command and recording check both succeed.
    IReadOnlyList<string>? UnverifiedHarnessSetups = null);

public interface IAppStateStore {
    Task<AppState> LoadAsync();

    /// Serialized read-modify-write; atomic temp+rename; false (logged) on write failure —
    /// caller keeps the claim in memory for the run.
    Task<bool> UpdateAsync(Func<AppState, AppState> mutate);
}

public sealed partial class AppStateStore(string path) : IAppStateStore {
    readonly SemaphoreSlim _gate = new(1, 1);

    // An uncontended gate completes synchronously, so the file I/O is pushed to the pool explicitly:
    // callers await these from the UI thread.
    public async Task<AppState> LoadAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            return await Task.Run(Read).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            if (await Task.Run(() => Write(mutate(Read()))).ConfigureAwait(false)) return true;

            Console.Error.WriteLine($"kcap: failed to persist app state to {path}");
            return false;
        } finally {
            _gate.Release();
        }
    }

    // Missing/corrupt → defaults; must never throw out of LoadAsync/UpdateAsync.
    AppState Read() {
        try {
            if (!File.Exists(path)) return new AppState();
            return JsonSerializer.Deserialize(File.ReadAllText(path), AppStateJsonContext.Default.AppState) ?? new AppState();
        } catch {
            return new AppState();
        }
    }

    // Temp write + rename: readers never observe a partial file. No fsync — this is UX state,
    // not durability-critical.
    bool Write(AppState state) {
        try {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, AppStateJsonContext.Default.AppState));
            File.Move(tmp, path, overwrite: true);
            return true;
        } catch {
            return false;
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(AppState))]
    partial class AppStateJsonContext : JsonSerializerContext;
}
