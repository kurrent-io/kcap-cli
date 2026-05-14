namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for <see cref="AgentLockPaths"/> — the name-sanitization rules and
/// the per-name path layout that prevents two daemons under the same name
/// from racing on the AI-630 lock.
/// </summary>
public class AgentLockPathsTests {
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
        await Assert.That(AgentLockPaths.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(AgentLockPaths.Sanitize(input)).IsEqualTo("daemon");
    }

    [Test]
    public async Task LockPidStart_ShareSanitizedName() {
        await Assert.That(AgentLockPaths.LockPath("My Daemon")).EndsWith("my-daemon.lock");
        await Assert.That(AgentLockPaths.PidPath("My Daemon")).EndsWith("my-daemon.pid");
        await Assert.That(AgentLockPaths.StartLockPath("My Daemon")).EndsWith("my-daemon.start");
    }
}
