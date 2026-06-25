using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class DaemonRestartMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            var when = new DateTimeOffset(2026, 6, 25, 12, 3, 0, TimeSpan.Zero);
            DaemonRestartMarker.Write("laptop", new DaemonRestartMarker("v0.4.11", "self-detected", when));

            var read = DaemonRestartMarker.TryRead("laptop");

            await Assert.That(read).IsNotNull();
            await Assert.That(read!.RunningVersion).IsEqualTo("v0.4.11");
            await Assert.That(read.Reason).IsEqualTo("self-detected");
            await Assert.That(read.QueuedAt).IsEqualTo(when);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            await Assert.That(DaemonRestartMarker.TryRead("nope")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            File.WriteAllText(DaemonLockPaths.RestartPendingPath("orphan"), "{}");
            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains("orphan");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }
}
