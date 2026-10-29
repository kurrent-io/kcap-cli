using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class WatcherManagerKillTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>A pid file another process holds exclusively cannot be read, so the kill fails with the
    /// watcher still alive. The file must survive it: without it, the retry finds nothing to kill and
    /// reads the running watcher as gone.</summary>
    [Test]
    public async Task A_failed_kill_keeps_the_pid_file_so_a_retry_kills_the_watcher() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

        using var watcher  = DummyProcess.StartSleep(30);
        var       identity = PidIdentity.Capture(watcher.Pid);
        var       pidFile  = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{identity}");

        bool failed;
        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failed = await watchers.KillWatcher("s-a");

        await Assert.That(failed).IsFalse();
        await Assert.That(File.Exists(pidFile)).IsTrue();
        await Assert.That(PidIdentity.IsGone(watcher.Pid, identity)).IsFalse();

        await Assert.That(await watchers.KillWatcher("s-a")).IsTrue();
        await Assert.That(File.Exists(pidFile)).IsFalse();
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
    }

    /// <summary>A wedged watcher whose reap fails is still running, so no second watcher may be spawned
    /// over its pid file; the next call retries the reap and only then spawns.</summary>
    [Test]
    public async Task A_wedged_watcher_whose_reap_fails_is_not_respawned_until_a_retry_stops_it() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var spawner  = new FakeWatcherSpawner();
        var watchers = new WatcherManager(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient(),
            SystemProcessStarter.Instance, paths, spawner, TimeProvider.System);

        using var watcher  = DummyProcess.StartSleep(30);
        var       identity = PidIdentity.Capture(watcher.Pid);
        var       pidFile  = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{identity}");
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        WatcherHeartbeat.Touch(paths.StartedFile("s-a"), longAgo);
        WatcherHeartbeat.Touch(paths.HeartbeatFile("s-a"), longAgo);

        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await watchers.EnsureWatcherRunning("s-a", "transcript.jsonl", agentId: "child");

        await Assert.That(spawner.Spawned).IsEmpty();
        await Assert.That(File.Exists(pidFile)).IsTrue();
        await Assert.That(PidIdentity.IsGone(watcher.Pid, identity)).IsFalse();

        await watchers.EnsureWatcherRunning("s-a", "transcript.jsonl", agentId: "child");

        await Assert.That(spawner.Keys).IsEquivalentTo(["s-a"]);
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
    }
}
