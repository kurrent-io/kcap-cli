using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

public sealed partial class NotificationSettingsService : IDisposable {
    readonly string _path;
    readonly BehaviorSubject<NotificationPreferences> _changes;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _lifecycle = new();
    readonly object _liveState = new();
    bool _disposeStarted;
    int _activeSaves;

    public NotificationSettingsService(string path) {
        _path = path;
        Current = Read();
        _changes = new BehaviorSubject<NotificationPreferences>(Current);
    }

    public NotificationPreferences Current { get; private set; }
    public IObservable<NotificationPreferences> Changes => _changes;

    public async Task<bool> SaveAsync(NotificationPreferences preferences) {
        lock (_lifecycle) {
            if (_disposeStarted) return false;
            _activeSaves++;
        }
        var entered = false;
        try {
            lock (_liveState) {
                Current = preferences;
                _changes.OnNext(preferences);
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            entered = true;
            string? tmp = null;
            try {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(tmp,
                    JsonSerializer.Serialize(preferences, NotificationSettingsJsonContext.Default.NotificationPreferences))
                    .ConfigureAwait(false);
                File.Move(tmp, _path, overwrite: true);
                return true;
            } catch {
                return false;
            } finally {
                if (tmp is not null) {
                    try { File.Delete(tmp); } catch { }
                }
            }
        } finally {
            if (entered) _gate.Release();
            lock (_lifecycle) {
                _activeSaves--;
                if (_activeSaves == 0) Monitor.PulseAll(_lifecycle);
            }
        }
    }

    NotificationPreferences Read() {
        try {
            if (!File.Exists(_path)) return new NotificationPreferences();
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            return new NotificationPreferences(
                ReadSwitch(json.RootElement, "permissions"),
                ReadSwitch(json.RootElement, "questions"),
                ReadSwitch(json.RootElement, "idle"));
        } catch {
            return new NotificationPreferences();
        }
    }

    static bool ReadSwitch(JsonElement root, string name) => root.Bool(name) ?? true;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(NotificationPreferences))]
    partial class NotificationSettingsJsonContext : JsonSerializerContext;

    public void Dispose() {
        lock (_lifecycle) {
            if (_disposeStarted) return;
            _disposeStarted = true;
            while (_activeSaves > 0) Monitor.Wait(_lifecycle);
        }
        _changes.Dispose();
        _gate.Dispose();
    }
}
