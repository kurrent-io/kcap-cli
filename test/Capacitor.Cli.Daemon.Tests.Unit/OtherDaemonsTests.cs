namespace Capacitor.Cli.Daemon.Tests.Unit;

/// The repo-list worktree sweep runs only when this daemon is the machine's only one, since another
/// daemon's live worktrees can share a repository. A live daemon is one whose lock is held.
public class OtherDaemonsTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task A_held_lock_is_another_live_daemon() {
        Daemons.Store.EnsureDirectory();
        await using var held = new FileStream(Daemons.Store.LockPath("other"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        await Assert.That(OtherDaemons.AnyAlive(Daemons.Store, "self")).IsTrue();
    }

    [Test]
    public async Task A_lock_file_nobody_holds_is_a_stopped_daemon() {
        Daemons.Store.EnsureDirectory();
        await File.WriteAllTextAsync(Daemons.Store.LockPath("other"), "instance");

        await Assert.That(OtherDaemons.AnyAlive(Daemons.Store, "self")).IsFalse();
        await Assert.That(File.Exists(Daemons.Store.LockPath("other"))).IsTrue();
    }

    [Test]
    public async Task This_daemons_own_lock_does_not_count() {
        Daemons.Store.EnsureDirectory();
        await using var own = new FileStream(Daemons.Store.LockPath("self"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        await Assert.That(OtherDaemons.AnyAlive(Daemons.Store, "self")).IsFalse();
    }
}
