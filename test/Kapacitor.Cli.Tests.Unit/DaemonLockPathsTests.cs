namespace Kapacitor.Cli.Tests.Unit;

/// <summary>
/// Tests for <see cref="Kapacitor.Cli.Core.DaemonLockPaths"/> — the name-sanitization rules and
/// the per-name path layout that prevents two daemons under the same name
/// from racing on the AI-630 lock.
/// </summary>
public class DaemonLockPathsTests {
    [Test]
    [Arguments("alexey",            "alexey")]
    [Arguments("ALEXEY",            "alexey")]
    [Arguments("My Daemon",         "my-daemon")]
    [Arguments("user@host",         "user-host")]
    [Arguments("a/b\\c",             "a-b-c")]
    [Arguments("  spaced  ",        "spaced")]
    [Arguments("dots.are.fine",     "dots.are.fine")]
    [Arguments("dashes-ok",         "dashes-ok")]
    [Arguments("under_score",       "under_score")] // underscores survive: filesystem-safe and common
    [Arguments("collapse---dashes", "collapse-dashes")]
    public async Task Sanitize_NormalisesNames(string input, string expected) {
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.Sanitize(input)).IsEqualTo("daemon");
    }

    [Test]
    public async Task LockPidStart_ShareSanitizedName() {
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.LockPath("My Daemon")).EndsWith("my-daemon.lock");
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.PidPath("My Daemon")).EndsWith("my-daemon.pid");
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.StartLockPath("My Daemon")).EndsWith("my-daemon.start");
    }

    [Test]
    public async Task Directory_LivesUnderDaemonsFolder() {
        // Verify the production layout (no test override) uses the renamed
        // ~/.config/kapacitor/daemons/ path, not the pre-AI-644 agents/ path.
        await Assert.That(Kapacitor.Cli.Core.DaemonLockPaths.Directory.Replace('\\', '/'))
            .EndsWith("/.config/kapacitor/daemons");
    }
}
