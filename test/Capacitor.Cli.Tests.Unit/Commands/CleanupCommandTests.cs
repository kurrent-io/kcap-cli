using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class CleanupCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>A watcher cleanup could not stop is still running, so it must not be reported as a
    /// stale pid file cleaned up, and the run must fail so uninstall keeps the directory it writes to.</summary>
    [Test]
    public async Task A_watcher_it_cannot_stop_fails_the_cleanup_and_keeps_its_pid_file() {
        var paths = new WatcherPaths(Config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var watchers = TestWatchers.In(paths, Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        int exit;
        using var capture = ConsoleOutput.StartCapture();

        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            exit = await new CleanupCommand(watchers).HandleCleanup();

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(pidFile)).IsTrue();
        await Assert.That(capture.GetCapturedOutput()).Contains("Could not stop watcher s-a");
        await Assert.That(capture.GetCapturedOutput()).DoesNotContain("Cleaned up stale PID file for s-a");
    }
}
