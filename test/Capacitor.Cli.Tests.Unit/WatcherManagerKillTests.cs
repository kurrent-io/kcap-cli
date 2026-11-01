using Capacitor.Cli.Core;
using TUnit.Core.Enums;

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

    /// <summary>A Windows watcher is asked to stop before anything kills it, so it drains and logs its exit
    /// the way SIGTERM lets it on Unix. The listener stands in for the watcher's own.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task A_windows_watcher_is_asked_to_stop_before_it_is_killed() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

        using var watcher = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(paths.PidFile("s-a"), $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var asked = new TaskCompletionSource();
        using var listener = WatcherStopSignal.Listen(watcher.Pid, () => {
            asked.TrySetResult();
            watcher.Kill();
        });

        await Assert.That(await watchers.KillWatcher("s-a", TextWriter.Null)).IsTrue();
        await Assert.That(asked.Task.IsCompleted).IsTrue();
    }

    /// <summary>A watcher asked to stop can arm a successor under its own key before it exits. The kill that
    /// asked must not then delete the successor's pid file, or the next call starts a second watcher.</summary>
    [Test, RunOn(OS.Windows)]
    public async Task A_successor_armed_while_stopping_keeps_its_pid_file() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

        using var watcher   = DummyProcess.StartSleep(30);
        using var successor = DummyProcess.StartSleep(30);
        var       pidFile   = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        using var listener = WatcherStopSignal.Listen(watcher.Pid, () => {
            File.WriteAllText(pidFile, $"{successor.Pid}\n{PidIdentity.Capture(successor.Pid)}");
            watcher.Kill();
        });

        await Assert.That(await watchers.KillWatcher("s-a", TextWriter.Null)).IsTrue();
        await Assert.That(File.ReadAllLines(pidFile)[0]).IsEqualTo(successor.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
