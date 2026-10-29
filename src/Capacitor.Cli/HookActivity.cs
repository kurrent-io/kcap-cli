using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// When each agent last ran a kcap hook on this machine, kept as one empty file per vendor whose
/// modified time is the answer. An agent that runs a hook has loaded it, and for Codex has trusted it,
/// which is what <c>kcap status</c> needs to tell "configured" apart from "recording".
/// </summary>
public sealed class HookActivity(ConfigRoot config, TimeProvider time) {
    /// <summary>Hooks fire many times a minute in a busy session; one write a minute is enough for a
    /// "last event" read in minutes, and keeps the hot path to a stat.</summary>
    static readonly TimeSpan StampInterval = TimeSpan.FromMinutes(1);

    string PathFor(string vendorId) => config.Path("hook-activity", vendorId);

    /// <summary>Best effort: a hook must never fail, or slow down, because this could not be written.</summary>
    public void Stamp(string vendorId) {
        try {
            var path = PathFor(vendorId);
            var now  = time.GetUtcNow().UtcDateTime;
            var file = new FileInfo(path);

            if (file.Exists && now - file.LastWriteTimeUtc < StampInterval) return;

            if (!file.Exists) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }

            File.SetLastWriteTimeUtc(path, now);
        } catch (Exception) { }
    }

    /// <summary>Null when the agent never ran a hook here, or when the stamp cannot be read: status
    /// reports what it can rather than failing over a best-effort file.</summary>
    public DateTimeOffset? LastEvent(string vendorId) {
        try {
            var file = new FileInfo(PathFor(vendorId));

            return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null;
        } catch (Exception) {
            return null;
        }
    }
}
