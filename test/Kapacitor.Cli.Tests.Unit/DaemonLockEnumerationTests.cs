namespace Kapacitor.Cli.Tests.Unit;

/// <summary>
/// AI-630 review fix #4: <see cref="Kapacitor.Cli.Core.DaemonLockPaths.EnumerateNames"/> must
/// union <c>*.lock</c> and <c>*.pid</c> filenames, not just <c>*.lock</c>.
/// An orphan PID file (no matching lock, e.g. a daemon that stopped via
/// the AI-78 path before the per-name layout existed) needs to be visible
/// to <c>kapacitor daemon doctor --clean</c>; previously it was invisible.
/// </summary>
[NotInParallel(nameof(Kapacitor.Cli.Core.DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonLockEnumerationTests {
    [Test]
    public async Task EnumerateNames_UnionsLockAndPidFiles() {
        var dir = Path.Combine(Path.GetTempPath(), "kapacitor-enum-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Kapacitor.Cli.Core.DaemonLockPaths.OverrideDirectoryForTesting(dir);

        try {
            // alpha has both lock and pid (held daemon).
            // beta has only a lock (e.g. doctor just cleaned the pid).
            // gamma has only a pid (orphan from before AI-630 migration).
            File.WriteAllText(Path.Combine(dir, "alpha.lock"), "instance-1");
            File.WriteAllText(Path.Combine(dir, "alpha.pid"),  "12345");
            File.WriteAllText(Path.Combine(dir, "beta.lock"),  "instance-2");
            File.WriteAllText(Path.Combine(dir, "gamma.pid"),  "67890");

            var names = Kapacitor.Cli.Core.DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(3);
            await Assert.That(names).Contains("alpha");
            await Assert.That(names).Contains("beta");
            await Assert.That(names).Contains("gamma");
        } finally {
            Kapacitor.Cli.Core.DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task EnumerateNames_DeduplicatesNamesAppearingInBoth() {
        var dir = Path.Combine(Path.GetTempPath(), "kapacitor-enum-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Kapacitor.Cli.Core.DaemonLockPaths.OverrideDirectoryForTesting(dir);

        try {
            File.WriteAllText(Path.Combine(dir, "alpha.lock"), "instance-1");
            File.WriteAllText(Path.Combine(dir, "alpha.pid"),  "12345");

            var names = Kapacitor.Cli.Core.DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(1);
            await Assert.That(names[0]).IsEqualTo("alpha");
        } finally {
            Kapacitor.Cli.Core.DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
