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

    /// <summary>An install reached through a symlinked root is the same install.</summary>
    [Test]
    public async Task Same_daemon_sees_through_a_symlinked_install_root() {
        Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs a privilege Windows CI does not grant");

        var daemon = Tmp.CreateFile(["kcap", "bin", "kcap-daemon"], "");
        var alias  = Tmp.PathTo("alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(Path.GetDirectoryName(daemon))!);

        await Assert.That(UnitIdentity.SameDaemon(Path.Combine(alias, "bin", "kcap-daemon"), daemon)).IsTrue();
    }

    /// <summary>Outside Windows a path differing only in case is another install: a case-sensitive
    /// volume can hold both, and calling them one would hide the drift.</summary>
    [Test]
    public async Task Same_daemon_counts_case_outside_windows() {
        Skip.When(OperatingSystem.IsWindows(), "Windows paths are case-insensitive");

        await Assert.That(UnitIdentity.SameDaemon("/opt/Kcap/bin/kcap-daemon", "/opt/kcap/bin/kcap-daemon")).IsFalse();
    }
}
