using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonLockPriorInstanceIdTests {
    static string Scratch() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-lock-prior", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        return dir;
    }

    [Test]
    public async Task FreshSlot_has_null_prior_instance_id() {
        var dir = Scratch();
        try {
            using var l = DaemonLock.TryAcquire("alpha");
            await Assert.That(l!.PriorInstanceId).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }

    [Test]
    public async Task NonEmpty_unreadable_prior_lock_is_indeterminate_not_a_fresh_slot() {
        var dir = Scratch();
        try {
            var first = DaemonLock.TryAcquire("alpha")!;
            var firstId = first.InstanceId;
            first.Dispose(); // lock file (with firstId) survives Dispose

            // Corrupt the lock file to non-empty blank content: a re-acquire must read it as INDETERMINATE
            // (priorInstanceId null AND PriorLockIndeterminate true) — NOT a genesis-eligible empty slot.
            var lockFile = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .First(f => File.ReadAllText(f).Contains(firstId));
            File.WriteAllText(lockFile, "   \n");

            using var second = DaemonLock.TryAcquire("alpha");
            await Assert.That(second!.PriorInstanceId).IsNull();
            await Assert.That(second.PriorLockIndeterminate).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }

    [Test]
    public async Task FreshSlot_is_not_indeterminate() {
        var dir = Scratch();
        try {
            using var l = DaemonLock.TryAcquire("alpha");
            await Assert.That(l!.PriorInstanceId).IsNull();
            await Assert.That(l.PriorLockIndeterminate).IsFalse(); // genuinely empty ⇒ genesis-eligible, not indeterminate
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ReAcquire_sees_the_previous_boots_instance_id() {
        var dir = Scratch();
        try {
            var first = DaemonLock.TryAcquire("alpha")!;
            var firstId = first.InstanceId;
            first.Dispose(); // lock file (with firstId) survives Dispose

            using var second = DaemonLock.TryAcquire("alpha");
            await Assert.That(second!.PriorInstanceId).IsEqualTo(firstId);
            await Assert.That(second.InstanceId).IsNotEqualTo(firstId);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); Directory.Delete(dir, true); }
    }
}
