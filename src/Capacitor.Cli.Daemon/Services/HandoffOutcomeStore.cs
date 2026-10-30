using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Each hand-off's takeover outcome by the successor's canonical session id, the newest
/// <see cref="Capacity"/> kept in one owner-only file. A <c>running</c> outcome found when the file is first
/// read was cut short by a restart and reads as <c>interrupted</c>: only the process that wrote it could still
/// finish it. A file that does not parse reads as empty and is replaced by the next put.</summary>
internal sealed partial class HandoffOutcomeStore(string stateDirectory, TimeProvider time) {
    public const int Capacity = 200;

    const string FileName = "handoff-outcomes.json";

    readonly string _path = Path.Combine(stateDirectory, FileName);
    readonly Lock   _lock = new();

    List<Entry>? _entries;

    /// <summary>Kept in memory even when the file cannot be written, so this process still serves it; the
    /// write failure is thrown.</summary>
    public void Put(string successorSessionId, HandoffOutcomeDto outcome) {
        if (SessionIds.Canonical(successorSessionId) is not { } key) return;

        lock (_lock) {
            var entries = Load();
            entries.RemoveAll(e => e.SessionId == key);
            entries.Add(new Entry(key, time.GetUtcNow(), outcome));
            if (entries.Count > Capacity) entries.RemoveRange(0, entries.Count - Capacity);

            Save(entries);
        }
    }

    public HandoffOutcomeDto? Get(string successorSessionId) {
        if (SessionIds.Canonical(successorSessionId) is not { } key) return null;

        lock (_lock) return Load().FindLast(e => e.SessionId == key)?.Outcome;
    }

    List<Entry> Load() {
        if (_entries is not null) return _entries;

        List<Entry>? read;
        try {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = JsonSerializer.Deserialize(stream, HandoffOutcomeJsonCtx.Default.ListEntry);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            read = null;
        }

        return _entries = [
            .. (read ?? [])
                .Where(e => e is { SessionId: not null, Outcome.Items: not null })
                .Select(e => e.Outcome.State == "running" ? e with { Outcome = e.Outcome with { State = "interrupted" } } : e)
        ];
    }

    void Save(List<Entry> entries) {
        if (!Directory.Exists(stateDirectory)) OwnerOnlyDirectory.Ensure(stateDirectory);

        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try {
            using (var stream = OwnerOnlyFile.CreateNew(temp)) {
                JsonSerializer.Serialize(stream, entries, HandoffOutcomeJsonCtx.Default.ListEntry);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        } catch {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            throw;
        }
    }

    internal sealed record Entry(string SessionId, DateTimeOffset UpdatedAt, HandoffOutcomeDto Outcome);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(List<Entry>))]
    partial class HandoffOutcomeJsonCtx : JsonSerializerContext;
}
