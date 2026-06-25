using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class RestartCoordinatorTests {
    DirectoryInfo? _dir;

    [Before(HookType.Test)]
    public void Setup() {
        _dir = Directory.CreateTempSubdirectory("kcap-coord-");
        DaemonLockPaths.OverrideDirectoryForTesting(_dir.FullName);
    }

    [After(HookType.Test)]
    public void Teardown() {
        DaemonLockPaths.OverrideDirectoryForTesting(null);
        try { _dir?.Delete(true); } catch { /* best-effort */ }
    }

    sealed class SpyStrategy : IRestartStrategy {
        public int Calls;
        public void Restart() => Calls++;
    }

    static RestartCoordinator NewCoordinator(SpyStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
        var c = RestartCoordinator.ForTest("laptop", "v0.4.11", spy);
        c.IsBusy     = isBusy;
        c.StatBinary = stat;
        c.PrimeBaseline();   // capture initial stat as baseline
        return c;
    }

    [Test]
    public async Task Binary_change_while_idle_triggers_restart() {
        var spy   = new SpyStrategy();
        var size  = 100L;
        var c     = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200; // simulate update landing
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_change_while_busy_waits_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // busy → queued, no fire
        await Assert.That(spy.Calls).IsEqualTo(0);
        busy = false;
        c.Tick();                                   // idle → fire
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Transient_stat_failure_does_not_queue() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => null);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_force_request_fires_even_when_busy() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => true, stat: () => new BinaryStat(100, 1));
        c.RequestRestart(force: true);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_only_fires_once() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }
}
