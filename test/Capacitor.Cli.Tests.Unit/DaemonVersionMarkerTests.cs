using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonVersionMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        var dir = Directory.CreateTempSubdirectory("kcap-ver-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            DaemonVersionMarker.Write("laptop", "0.4.11+sha.abc1234");

            await Assert.That(DaemonVersionMarker.TryRead("laptop")).IsEqualTo("0.4.11+sha.abc1234");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        var dir = Directory.CreateTempSubdirectory("kcap-ver-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            await Assert.That(DaemonVersionMarker.TryRead("nope")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task TryRead_returns_null_for_blank_marker() {
        var dir = Directory.CreateTempSubdirectory("kcap-ver-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            File.WriteAllText(DaemonLockPaths.VersionPath("laptop"), "   \n");

            await Assert.That(DaemonVersionMarker.TryRead("laptop")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task Delete_removes_marker() {
        var dir = Directory.CreateTempSubdirectory("kcap-ver-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            DaemonVersionMarker.Write("laptop", "0.4.11");
            DaemonVersionMarker.Delete("laptop");

            await Assert.That(File.Exists(DaemonLockPaths.VersionPath("laptop"))).IsFalse();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        var dir = Directory.CreateTempSubdirectory("kcap-ver-");
        DaemonLockPaths.OverrideDirectoryForTesting(dir.FullName);
        try {
            DaemonVersionMarker.Write("orphan", "0.4.11");

            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains("orphan");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            dir.Delete(true);
        }
    }
}
