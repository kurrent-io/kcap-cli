namespace Capacitor.Cli.Commands;

static class CleanupCommand {
    public static async Task<int> HandleCleanup() {
        // Honor the KCAP_WATCHER_DIR override (via GetWatcherDir) so cleanup targets the same
        // directory KillWatcher/SpawnWatcher use, rather than always the config default.
        var watcherDir = WatcherManager.GetWatcherDir();

        if (!Directory.Exists(watcherDir)) {
            await Console.Out.WriteLineAsync("No watchers directory found.");

            return 0;
        }

        var pidFiles = Directory.GetFiles(watcherDir, "*.pid");

        var killed  = 0;
        var cleaned = 0;

        foreach (var pidFile in pidFiles) {
            var key        = Path.GetFileNameWithoutExtension(pidFile);
            var wasRunning = await WatcherManager.KillWatcher(key);

            if (wasRunning) {
                await Console.Out.WriteLineAsync($"Killed watcher {key}");
                killed++;
            } else {
                await Console.Out.WriteLineAsync($"Cleaned up stale PID file for {key}");
                cleaned++;
            }
        }

        // Sweep any leftover per-key auxiliary files (heartbeat/started/spawnlock). KillWatcher
        // removes the heartbeat/started markers per key but deliberately leaves spawn locks
        // behind (unlink-race safety); cleanup holds no lock, so it's the safe place to purge
        // them, and this also mops up orphans whose .pid was already gone.
        var purged = WatcherManager.PurgeAuxiliaryFiles();

        await Console.Out.WriteLineAsync(
            $"Done. Killed {killed} watcher(s), cleaned {cleaned} stale PID file(s), purged {purged} auxiliary file(s).");

        return 0;
    }
}
