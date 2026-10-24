namespace Capacitor.Cli.Commands;

sealed class CleanupCommand(WatcherManager watchers, StandardStreams console) {

    public async Task<int> HandleCleanup() {
        // The directory WatcherPaths resolves, not the config default: a spawn writes its pid
        // files wherever KCAP_WATCHER_DIR points, and cleanup has to look in that same place.
        var watcherDir = watchers.GetWatcherDir();

        if (!Directory.Exists(watcherDir)) {
            await console.Out.WriteLineAsync("No watchers directory found.");

            return 0;
        }

        var pidFiles = Directory.GetFiles(watcherDir, "*.pid");

        var killed  = 0;
        var cleaned = 0;
        var failed  = 0;

        foreach (var pidFile in pidFiles) {
            var key        = Path.GetFileNameWithoutExtension(pidFile);
            var wasRunning = await watchers.KillWatcher(key, console.Error);

            if (wasRunning) {
                await console.Out.WriteLineAsync($"Killed watcher {key}");
                killed++;
            } else if (File.Exists(pidFile)) {
                await console.Out.WriteLineAsync($"Could not stop watcher {key}; kept its PID file");
                failed++;
            } else {
                await console.Out.WriteLineAsync($"Cleaned up stale PID file for {key}");
                cleaned++;
            }
        }

        // Sweep any leftover per-key auxiliary files (heartbeat/started/spawnlock). KillWatcher
        // removes the heartbeat/started markers per key but deliberately leaves spawn locks
        // behind (unlink-race safety); cleanup holds no lock, so it's the safe place to purge
        // them, and this also mops up orphans whose .pid was already gone.
        var purged = watchers.PurgeAuxiliaryFiles();

        await console.Out.WriteLineAsync(
            $"Done. Killed {killed} watcher(s), cleaned {cleaned} stale PID file(s), purged {purged} auxiliary file(s).");

        if (failed > 0) {
            await console.Out.WriteLineAsync($"{failed} watcher(s) could not be stopped; run kcap cleanup again.");

            return 1;
        }

        return 0;
    }
}
