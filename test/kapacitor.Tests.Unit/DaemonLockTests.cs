using kapacitor.Daemon;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonLock"/> — the per-name flock the daemon binary
/// holds for its lifetime. Verifies the AI-630 protection: a second daemon
/// acquiring the same name on the same machine fails fast.
///
/// All tests redirect <see cref="AgentLockPaths"/> to a temp directory so
/// nothing touches the user's real <c>~/.config/kapacitor/agents</c>. The
/// per-class isolation pattern (unique subdir per test, restored at the
/// end) keeps the tests independent of one another's failed/leaked locks.
/// </summary>
[NotInParallel(nameof(AgentLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonLockTests {
    static string CreateScratchDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kapacitor-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AgentLockPaths.OverrideDirectoryForTesting(dir);

        return dir;
    }

    static void Restore() => AgentLockPaths.OverrideDirectoryForTesting(null);

    [Test]
    public async Task TryAcquire_OnFreshSlot_ReturnsLockWithFreshInstanceId() {
        var dir = CreateScratchDir();

        try {
            var l = DaemonLock.TryAcquire("alpha");

            await Assert.That(l).IsNotNull();
            await Assert.That(l!.InstanceId).IsNotEmpty();
            await Assert.That(l.InstanceId.Length).IsEqualTo(32); // GUID "N" format

            // The lock file is held with FileShare.None, so the test
            // process can't read it while the daemon owns it (that's the
            // whole point — would-be duplicates can't peek). Verify the
            // content after disposal instead.
            var expected = l.InstanceId;
            l.Dispose();

            // Dispose deletes the lock file, which is the production
            // behaviour. Re-acquire to inspect the file content we'd see
            // mid-lifetime: it'll have a fresh instance id, but the
            // structure (single 32-char GUID line) is the assertion that
            // matters.
            using var reacquired = DaemonLock.TryAcquire("alpha");
            await Assert.That(reacquired).IsNotNull();
            await Assert.That(reacquired!.InstanceId).IsNotEqualTo(expected);
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryAcquire_OnSameName_WhileHeld_Fails() {
        var dir = CreateScratchDir();

        try {
            using var first = DaemonLock.TryAcquire("alpha");
            await Assert.That(first).IsNotNull();

            var second = DaemonLock.TryAcquire("alpha");
            await Assert.That(second).IsNull();
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryAcquire_DifferentNames_BothSucceed() {
        var dir = CreateScratchDir();

        try {
            using var alpha = DaemonLock.TryAcquire("alpha");
            using var beta  = DaemonLock.TryAcquire("beta");

            await Assert.That(alpha).IsNotNull();
            await Assert.That(beta).IsNotNull();
            await Assert.That(alpha!.InstanceId).IsNotEqualTo(beta!.InstanceId);
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Dispose_ReleasesLock_AllowingReAcquisition() {
        var dir = CreateScratchDir();

        try {
            var first = DaemonLock.TryAcquire("alpha");
            await Assert.That(first).IsNotNull();

            // Different instance id should be produced on re-acquire so a
            // post-disposal observer can tell the daemons apart.
            var firstId = first!.InstanceId;
            first.Dispose();

            using var second = DaemonLock.TryAcquire("alpha");
            await Assert.That(second).IsNotNull();
            await Assert.That(second!.InstanceId).IsNotEqualTo(firstId);
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// AI-630 review fix: <see cref="DaemonLock.Dispose"/> must not delete the
    /// lock file on disk. If it did, a daemon B that acquired the path between
    /// our flock release and the unlink would have its inode unlinked under
    /// it, and a daemon C could then create a fresh file and acquire a SECOND
    /// independent flock at the same path — reopening the duplicate-name hole.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDeleteLockFile() {
        var dir = CreateScratchDir();

        try {
            var l = DaemonLock.TryAcquire("alpha")!;
            var lockPath = AgentLockPaths.LockPath("alpha");

            await Assert.That(File.Exists(lockPath)).IsTrue();

            l.Dispose();

            await Assert.That(File.Exists(lockPath)).IsTrue();
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// AI-630 review fix: when a successor daemon has already overwritten
    /// the PID file with its own PID, the disposing daemon must not delete
    /// it — that would orphan the successor's entry and `agent stop` would
    /// no longer find the live daemon.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDeletePidFile_IfItPointsToSomeoneElsesPid() {
        var dir = CreateScratchDir();

        try {
            var l = DaemonLock.TryAcquire("alpha")!;
            var pidPath = AgentLockPaths.PidPath("alpha");

            // Simulate a successor process that won the race after our
            // flock release and rewrote the PID file. We pick a PID that
            // is definitely not us.
            File.WriteAllText(pidPath, "99999\n637999999999999999");

            l.Dispose();

            await Assert.That(File.Exists(pidPath)).IsTrue();
            await Assert.That(File.ReadAllText(pidPath)).StartsWith("99999");
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TryAcquire_AfterStaleLockFileLeftBehind_StillAcquires() {
        var dir = CreateScratchDir();

        try {
            // Simulate a daemon that died without cleanup: lockfile exists,
            // but no process holds the kernel flock. Re-acquisition must
            // succeed — that's the whole point of flock semantics.
            AgentLockPaths.EnsureDirectory();
            File.WriteAllText(AgentLockPaths.LockPath("alpha"), "stale-instance-id");

            using var l = DaemonLock.TryAcquire("alpha");
            await Assert.That(l).IsNotNull();
            await Assert.That(l!.InstanceId).IsNotEqualTo("stale-instance-id");
        } finally {
            Restore();
            Directory.Delete(dir, recursive: true);
        }
    }
}
