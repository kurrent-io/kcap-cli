using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class UnitIdentityTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A unit pinned to a version directory runs the same install as one on `current`; a
    /// sibling install with the same layout does not.</summary>
    [Test]
    public async Task Same_daemon_reads_a_version_directory_as_its_current_path() {
        Tmp.CreateFile(["kcap", ScriptInstallLayout.MarkerFileName], "{}");
        var versioned = Tmp.CreateFile(["kcap", "versions", "1.3.0", "bin", "kcap-daemon"], "");
        var current   = Tmp.CreateFile(["kcap", "current", "bin", "kcap-daemon"], "");
        var elsewhere = Tmp.CreateFile(["npm", "bin", "kcap-daemon"], "");

        await Assert.That(UnitIdentity.SameDaemon(versioned, current)).IsTrue();
        await Assert.That(UnitIdentity.SameDaemon(elsewhere, current)).IsFalse();
        await Assert.That(UnitIdentity.SameDaemon(null, current)).IsFalse();
    }
}
