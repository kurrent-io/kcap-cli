using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class CleanupCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>A watcher cleanup could not stop is still running, so it must not be reported as a
    /// stale pid file cleaned up, and the run must fail so uninstall keeps the directory it writes to.
    /// Why the kill failed goes to the command's own stderr.</summary>
    [Test]
    public async Task A_watcher_it_cannot_stop_fails_the_cleanup_and_keeps_its_pid_file() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        int exit;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            exit = await new CleanupCommand(watchers, new StandardStreams(stdout, stderr, TextReader.Null)).HandleCleanup();

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(pidFile)).IsTrue();
        await Assert.That(stdout.ToString()).Contains("Could not stop watcher s-a");
        await Assert.That(stdout.ToString()).DoesNotContain("Cleaned up stale PID file for s-a");
        await Assert.That(stderr.ToString()).Contains("Error killing watcher s-a");
    }
}
