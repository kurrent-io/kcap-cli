using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnLockTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Acquire_release_and_probe() {
        await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsFalse();
        var l = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "a", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(l).IsNotNull();
        try {
            await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsTrue();
            await Assert.That(await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "a", TimeSpan.FromMilliseconds(50), TimeProvider.System)).IsNull();
        } finally {
            l!.Dispose();
        }

        await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsFalse();
        await Assert.That(File.Exists(Daemons.Store.ServiceLockPath("a"))).IsTrue();
    }

    [Test]
    public async Task Creates_missing_lock_directory() {
        var lockDir = Daemons.PathTo("nonexistent-subdir");
        var paths   = new DaemonStore(lockDir);

        var l = await ServiceTxnLock.TryAcquireAsync(paths, "b", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(l).IsNotNull();
        try {
            await Assert.That(Directory.Exists(lockDir)).IsTrue();
        } finally {
            l!.Dispose();
        }
    }

    /// <summary>The gap between attempts is drawn from the caller's clock too: while that clock
    /// stands still the attempt never retries, so a lock released in the meantime goes unnoticed. A
    /// gap slept on the wall clock would pick it up within one poll, which is the whole defect.</summary>
    [Test]
    public async Task Retry_gap_is_drawn_from_the_clock_it_was_given() {
        var time = new FakeTimeProvider();

        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "gap", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();

        var pending = ServiceTxnLock.TryAcquireAsync(Daemons.Store, "gap", TimeSpan.FromSeconds(10), time);
        held!.Dispose();

        // Five times the poll gap, so a wall-clock sleep has retried and taken the free lock by now.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await Assert.That(pending.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        using var acquired = await pending;
        await Assert.That(acquired).IsNotNull();
    }

    /// <summary>The wait budget is measured on the caller's clock: a held lock keeps the attempt
    /// pending while that clock stands still, and yields null only once it has passed the deadline.
    /// A deadline that dropped the budget would give up on the first contended attempt instead.</summary>
    [Test]
    public async Task Contention_wait_is_bounded_by_the_clock_it_was_given() {
        var time = new FakeTimeProvider();

        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "c", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();

        var pending = ServiceTxnLock.TryAcquireAsync(Daemons.Store, "c", TimeSpan.FromSeconds(10), time);
        await Assert.That(pending.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromSeconds(11));

        await Assert.That(await pending).IsNull();
    }

    [Test]
    public async Task Distinct_from_daemon_lock_path() {
        await Assert.That(Daemons.Store.ServiceLockPath("a"))
            .IsNotEqualTo(Daemons.Store.LockPath("a"));
    }
}
