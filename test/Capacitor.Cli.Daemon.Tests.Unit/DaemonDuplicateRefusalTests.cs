using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// A second daemon under a held name refuses through the lock instead of dying on the log file the first
/// one holds. Windows opens that file write-exclusive, so only the Windows leg can fail this test, and a
/// crash there is what a supervisor relaunches forever
[NotInParallel]
public class DaemonDuplicateRefusalTests {
    [Test]
    public async Task Held_lock_and_held_log_file_refuse_with_the_lock_exit() {
        using var store  = new TempDaemonStore();
        using var config = new TempConfigRoot();
        using var daemonsDir = EnvScope.Exclusive(DaemonStore.DaemonsDirEnvVar, store.Directory);
        using var configDir  = EnvScope.Exclusive(ConfigRoot.ConfigDirEnvVar, config.Directory);
        using var url        = EnvScope.Exclusive(ProfileOverrides.UrlVar, "https://kcap.example.invalid");
        using var supervised = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", null);

        using var first = DaemonLock.TryAcquire(store.Store, "dup");
        await Assert.That(first).IsNotNull();
        var log = config.PathTo("daemon-dup.log");
        await using var firstLog = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var stderr = ConsoleOutput.StartErrorCapture();

        var exit = await DaemonRunner.RunAsync(["--name", "dup", "--log-file", log]);

        await Assert.That(exit).IsEqualTo(DaemonRunner.LockRefusalExit(supervised: false));
        await Assert.That(stderr.GetCapturedError()).Contains("Another kcap-daemon is already running under the name 'dup'");
    }
}
