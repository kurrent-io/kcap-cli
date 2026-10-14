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
}
