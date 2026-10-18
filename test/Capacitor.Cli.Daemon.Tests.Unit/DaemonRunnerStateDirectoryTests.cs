using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonRunnerStateDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task The_daemon_state_directory_gets_a_protected_owner_only_DACL() {
        var stateDir = new DaemonStore(Tmp.Path).StateDirectory("d1");
        var warnings = new StringWriter();

        DaemonRunner.PrepareStateDirectory(stateDir, warnings);

        await Assert.That(warnings.ToString()).IsEmpty();
        await Assert.That(WindowsAcl.IsProtected(stateDir)).IsTrue();
        await Assert.That(WindowsAcl.Identities(stateDir)).IsEquivalentTo(WindowsAcl.OwnerOnly);
    }

    /// <summary>A file where the directory belongs fails the create on every platform, standing in for
    /// a location that refuses an ACL or mode.</summary>
    [Test]
    public async Task A_state_directory_that_cannot_be_secured_warns_instead_of_throwing() {
        var stateDir = Tmp.CreateFile("d1", "not a directory");
        var warnings = new StringWriter();

        DaemonRunner.PrepareStateDirectory(stateDir, warnings);

        await Assert.That(warnings.ToString()).Contains("could not make the state directory");
        await Assert.That(warnings.ToString()).Contains(stateDir);
    }
}
