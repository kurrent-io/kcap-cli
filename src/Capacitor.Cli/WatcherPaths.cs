using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Where a watcher's per-key files live. Resolved once and handed around, so the probe, the spawn
/// and <c>kcap cleanup</c> cannot disagree about which directory they are talking about.
/// </summary>
public sealed class WatcherPaths(string directory) {
    public const string DirEnvVar = "KCAP_WATCHER_DIR";

    public static WatcherPaths FromEnvironment(ConfigRoot config) =>
        new(Environment.GetEnvironmentVariable(DirEnvVar) ?? config.Path("watchers"));

    public string Directory => directory;

    public string PidFile(string key)       => Path.Combine(directory, $"{key}.pid");
    public string StartedFile(string key)   => Path.Combine(directory, $"{key}.started");
    public string SpawnLockFile(string key) => Path.Combine(directory, $"{key}.spawnlock");

    /// <summary>
    /// Touched every main-loop iteration by the watcher itself — see <c>WatchCommand.RunWatch</c> —
    /// so a probe can tell a wedged watcher from a healthy one.
    /// </summary>
    public string HeartbeatFile(string key) => WatcherHeartbeat.HeartbeatPath(directory, key);
}
