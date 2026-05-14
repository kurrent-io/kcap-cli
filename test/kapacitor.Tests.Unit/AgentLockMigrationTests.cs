namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for <see cref="AgentLockMigration"/> — the one-shot move of
/// pre-AI-630 singleton files (<c>agent.pid</c>, <c>agent.start.lock</c>) to
/// the new per-name layout. The migration must be best-effort (no throws),
/// idempotent (safe to call repeatedly), and prefer the new path's content
/// when both exist (the new file is authoritative because a fresh daemon
/// wrote it after the old one died).
///
/// All tests use the internal <see cref="AgentLockMigration.MigrateLegacyFiles(string, string, string)"/>
/// overload that accepts explicit legacy paths. Production code uses the
/// public overload backed by <c>PathHelpers.ConfigPath</c>, but injecting
/// legacy paths here keeps the test from cascading into PathHelpers'
/// once-cached <c>ConfigDir</c> static-readonly field (which would then
/// affect every other test in the suite that reads
/// <c>PathHelpers.ConfigPath</c>).
/// </summary>
[NotInParallel(nameof(AgentLockPaths) + ".OverrideDirectoryForTesting")]
public class AgentLockMigrationTests {
    static (string Scratch, string NewAgentsDir, string LegacyPid, string LegacyLock) Setup() {
        var scratch = Path.Combine(Path.GetTempPath(), "kapacitor-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        var newAgentsDir = Path.Combine(scratch, "agents");
        Directory.CreateDirectory(newAgentsDir);
        AgentLockPaths.OverrideDirectoryForTesting(newAgentsDir);

        var legacyPid  = Path.Combine(scratch, "agent.pid");
        var legacyLock = Path.Combine(scratch, "agent.start.lock");

        return (scratch, newAgentsDir, legacyPid, legacyLock);
    }

    static void TearDown(string scratch) {
        AgentLockPaths.OverrideDirectoryForTesting(null);
        try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public async Task MigrateLegacyFiles_MovesPidAndLockToNewLayout() {
        var (scratch, _, legacyPid, legacyLock) = Setup();

        try {
            File.WriteAllText(legacyPid,  "12345\n637899999999999999");
            File.WriteAllText(legacyLock, "");

            var moved = AgentLockMigration.MigrateLegacyFiles("alexey", legacyPid, legacyLock);

            await Assert.That(moved).Count().IsEqualTo(2);
            await Assert.That(File.Exists(legacyPid)).IsFalse();
            await Assert.That(File.Exists(legacyLock)).IsFalse();
            await Assert.That(File.Exists(AgentLockPaths.PidPath("alexey"))).IsTrue();
            await Assert.That(File.Exists(AgentLockPaths.StartLockPath("alexey"))).IsTrue();
            await Assert.That(File.ReadAllText(AgentLockPaths.PidPath("alexey"))).IsEqualTo("12345\n637899999999999999");
        } finally {
            TearDown(scratch);
        }
    }

    [Test]
    public async Task MigrateLegacyFiles_IsIdempotent_WithNoLegacyPresent() {
        var (scratch, _, legacyPid, legacyLock) = Setup();

        try {
            var moved = AgentLockMigration.MigrateLegacyFiles("alexey", legacyPid, legacyLock);
            await Assert.That(moved).IsEmpty();

            var moved2 = AgentLockMigration.MigrateLegacyFiles("alexey", legacyPid, legacyLock);
            await Assert.That(moved2).IsEmpty();
        } finally {
            TearDown(scratch);
        }
    }

    [Test]
    public async Task MigrateLegacyFiles_WhenNewPathAlreadyPopulated_DropsLegacy() {
        var (scratch, _, legacyPid, legacyLock) = Setup();

        try {
            // A fresh daemon already wrote to the new path. The legacy file
            // from an earlier dead daemon must be discarded, not overwriting
            // the new authoritative file.
            File.WriteAllText(AgentLockPaths.PidPath("alexey"), "FRESH");
            File.WriteAllText(legacyPid, "STALE");

            AgentLockMigration.MigrateLegacyFiles("alexey", legacyPid, legacyLock);

            await Assert.That(File.Exists(legacyPid)).IsFalse();
            await Assert.That(File.ReadAllText(AgentLockPaths.PidPath("alexey"))).IsEqualTo("FRESH");
        } finally {
            TearDown(scratch);
        }
    }
}
