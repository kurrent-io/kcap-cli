using System.Text.Json;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>On-disk shape of <c>telemetry.json</c>.</summary>
public readonly record struct TelemetryStateFile(bool? Enabled, bool NoticeShown);

/// <summary>
/// Owns <c>telemetry.json</c> in the CLI config directory: the persisted enable flag and the
/// first-run-notice marker. The anonymous device id lives in its own, lock-free file instead (see
/// <see cref="TelemetryDeviceId"/>) — this file keeps only the fields where a lost update actually
/// matters: a dropped <see cref="SetEnabled"/> silently clobbers a user's opt-out, where a device-id
/// write race just means one of two equally-fine GUIDs wins.
///
/// Deliberately NOT <see cref="MachineId"/>'s <c>machine.json</c> either. That file is an
/// auth-relevant identifier sent to the Capacitor server to prove machine identity; an analytics
/// id is a different purpose with a different lifetime.
///
/// Each mutation (toggling enabled, marking notice shown) acquires a cross-process lock and
/// performs its read-modify-write atomically inside the lock to prevent lost-update races. On
/// lock-acquisition failure, degrades to best-effort unlocked write rather than silently dropping
/// the change — this is critical for opt-out enforcement, where a lost SetEnabled(false) would be
/// a privacy failure.
/// </summary>
public static class TelemetryState {
    const string StateFileName = "telemetry.json";

    static string StatePath(ConfigRoot config) => config.Path(StateFileName);

    public static TelemetryStateFile Read(ConfigRoot config) => TryRead(config) ?? default;   // corrupt or locked → defaults, never throw

    /// <summary>The saved state, defaults when nothing is saved, or null when a saved file cannot be read:
    /// for a caller that must not mistake an opt-out it cannot read for no choice at all.</summary>
    public static TelemetryStateFile? TryRead(ConfigRoot config) {
        // Read outright rather than asked whether it exists, which answers false for a file it cannot see.
        try {
            var json = File.ReadAllText(StatePath(config));
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.TelemetryStateFile);
        } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            return default(TelemetryStateFile);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>The saved choice; false when the file cannot be read, since it may hold an opt-out.</summary>
    public static bool? PersistedEnabled(ConfigRoot config) => TryRead(config) is { } state ? state.Enabled : false;

    /// <summary>
    /// Persists the enable flag. Disabling also deletes the device id file (see
    /// <see cref="TelemetryDeviceId.Delete"/>): the spec's rationale for keeping the analytics id
    /// separate from <c>machine.json</c> is that opt-out can delete it outright, not merely stop
    /// minting new ones. Re-enabling later mints a fresh id on the next
    /// <see cref="TelemetryDeviceId.GetOrCreate"/> call, which is more private than resurrecting the
    /// discarded one.
    /// </summary>
    /// <returns>Whether the choice was written: every write failure is swallowed, so a caller that must
    /// not report an unsaved choice as saved reads it here.</returns>
    public static bool SetEnabled(bool enabled, ConfigRoot config) {
        var saved = Mutate(config, state => (state ?? default) with { Enabled = enabled });
        if (!enabled) {
            TelemetryDeviceId.Delete(config);
            // Events another process spooled before the opt-out would otherwise go out after a later opt-in.
            TelemetrySpool.For(config).Clear();
        }

        return saved;
    }

    /// <returns>Whether this call set the marker: false when it was already set, so of processes racing
    /// past the notice only one sees true.</returns>
    public static bool MarkNoticeShown(ConfigRoot config) {
        var set = false;
        // A file it cannot read may hold an opt-out, which writing defaults over it would erase.
        var saved = Mutate(config, state => {
            if (state is not { } current || current.NoticeShown) return null;
            set = true;
            return current with { NoticeShown = true };
        });
        return saved && set;
    }

    /// <summary>
    /// Acquires a cross-process lock, reads current state, applies the mutation, and writes
    /// back atomically if the mutation returns a non-null result. Returning null from the
    /// delegate signals "no change needed"; no write occurs in either the locked or fallback path.
    /// On lock-acquisition failure, falls back to unlocked read-modify-write to ensure changes
    /// like SetEnabled(false) are never silently dropped.
    /// </summary>
    static bool Mutate(ConfigRoot config, Func<TelemetryStateFile?, TelemetryStateFile?> apply) {
        var path = StatePath(config);
        var dir = Path.GetDirectoryName(path)!;

        try {
            using (config.AcquireLock(StateFileName, TimeSpan.FromSeconds(2))) {
                Directory.CreateDirectory(dir);
                var currentState = ReadLocked(path);
                var newState = apply(currentState);
                return !newState.HasValue || WriteLocked(path, newState.Value);
            }
        } catch (Exception) {
            // Lock acquisition failed (timeout, foreign-owned mutex, path errors, etc.)
            // or an exception escaped from inside the lock (should not happen, but catch broadly
            // per global constraint: telemetry must never throw to NativeAOT runtime).
            // Degrade to unlocked read-modify-write rather than silently dropping the change.
            return MutateUnlocked(path, dir, apply);
        }
    }

    static bool MutateUnlocked(string path, string dir, Func<TelemetryStateFile?, TelemetryStateFile?> apply) {
        try {
            Directory.CreateDirectory(dir);
            var currentState = ReadLocked(path);
            var newState = apply(currentState);
            // Note: without cross-process locking in this fallback path, two concurrent writers can
            // race read-modify-write and last-writer-wins on disk, dropping whichever change lost.
            // This is acceptable as a graceful degradation when the lock mechanism is unavailable,
            // and is documented so future readers don't conclude the re-read was omitted by oversight.
            return !newState.HasValue || WriteLocked(path, newState.Value);
        } catch (Exception) {
            // Best effort. Telemetry must never throw to the NativeAOT runtime.
            return false;
        }
    }

    /// Null when a saved file cannot be read, so a mutation can tell it from no file at all.
    static TelemetryStateFile? ReadLocked(string path) {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.TelemetryStateFile);
        } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            return default(TelemetryStateFile);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>
    /// Writes atomically: serialise to a temp file in the SAME directory, then rename over the
    /// target. <see cref="Read"/> takes no lock (locking every command-startup read would put a
    /// cross-process mutex on the hot path), so it relies on never observing a half-written file.
    /// A plain <c>File.WriteAllText(path, ...)</c> truncates the target before rewriting it — a
    /// concurrent unlocked <see cref="Read"/> landing in that window sees partial JSON, hits its
    /// catch, and returns <c>default</c> (<c>Enabled = null</c>), which
    /// <see cref="TelemetrySettings.Resolve(bool?, UnsavedConsent)"/> treats as "no persisted
    /// choice", falling back to the edition default and undoing an opt-out a process just saved. The temp file lives
    /// next to the target so the rename stays on one volume (required for it to be atomic), and a
    /// reader then only ever sees the fully-old or fully-new file, never a torn one.
    /// </summary>
    static bool WriteLocked(string path, TelemetryStateFile state) {
        var dir      = Path.GetDirectoryName(path) ?? "";
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        try {
            var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. Clean up the temp file so a failed write doesn't leave debris behind —
            // itself best-effort, since we're already in the never-throw fallback path.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return false;
        }
    }
}
