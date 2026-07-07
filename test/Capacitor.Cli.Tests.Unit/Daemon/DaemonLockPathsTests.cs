using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Tests for <see cref="DaemonLockPaths"/> — the name-sanitization rules and
/// the per-name path layout that prevents two daemons under the same name
/// from racing on the AI-630 lock.
/// </summary>
public class DaemonLockPathsTests {
    [Test]
    [Arguments("alexey", "alexey")]
    [Arguments("ALEXEY", "alexey")]
    [Arguments("My Daemon", "my-daemon")]
    [Arguments("user@host", "user-host")]
    [Arguments("a/b\\c", "a-b-c")]
    [Arguments("  spaced  ", "spaced")]
    [Arguments("dots.are.fine", "dots.are.fine")]
    [Arguments("dashes-ok", "dashes-ok")]
    [Arguments("under_score", "under_score")] // underscores survive: filesystem-safe and common
    [Arguments("collapse---dashes", "collapse-dashes")]
    public async Task Sanitize_NormalisesNames(string input, string expected) {
        await Assert.That(DaemonLockPaths.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(DaemonLockPaths.Sanitize(input)).IsEqualTo("daemon");
    }

    [Test]
    public async Task LockPidStart_ShareSanitizedName() {
        await Assert.That(DaemonLockPaths.LockPath("My Daemon")).EndsWith("my-daemon.lock");
        await Assert.That(DaemonLockPaths.PidPath("My Daemon")).EndsWith("my-daemon.pid");
        await Assert.That(DaemonLockPaths.StartLockPath("My Daemon")).EndsWith("my-daemon.start");
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Directory_LivesUnderDaemonsFolder() {
        // Verify the PRODUCTION fallback (no override, no KCAP_DAEMONS_DIR) uses the renamed
        // ~/.config/kcap/daemons/ path, not the pre-AI-644 agents/ path. DaemonPathsGlobalSetup
        // pins KCAP_DAEMONS_DIR to a temp path assembly-wide (so no test can reach the real
        // daemon), so clear both here to observe the fallback — synchronously, with NO await
        // between clear and restore, so the real dir is never visible to a parallel test.
        var savedEnv = Environment.GetEnvironmentVariable(DaemonLockPaths.DaemonsDirEnvVar);
        string resolved;
        DaemonLockPaths.OverrideDirectoryForTesting(null);
        Environment.SetEnvironmentVariable(DaemonLockPaths.DaemonsDirEnvVar, null);
        try {
            resolved = DaemonLockPaths.Directory;
        } finally {
            Environment.SetEnvironmentVariable(DaemonLockPaths.DaemonsDirEnvVar, savedEnv);
        }

        await Assert.That(resolved.Replace('\\', '/')).EndsWith("/.config/kcap/daemons");
    }
}
