using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RestartCoordinatorTests {
    // Per-test isolation for the restart marker the coordinator writes.
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    sealed class SpyStrategy : IRestartStrategy {
        public int            Calls;
        public RestartOutcome Outcome = RestartOutcome.Initiated;
        public RestartOutcome Restart() { Calls++; return Outcome; }
    }

    RestartCoordinator NewCoordinator(SpyStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
        var c = RestartCoordinator.ForTest(Daemons.Store, "laptop", "v0.4.11", spy, TimeProvider.System);
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

    [Test]
    public async Task Failed_restart_is_retried_on_next_tick() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.Retry };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // fires, strategy returns Retry → un-claimed
        await Assert.That(spy.Calls).IsEqualTo(1);
        c.Tick();                                   // still pending → retries
        await Assert.That(spy.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Noop_outcome_is_terminal() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.NoOp };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);  // NoOp is terminal — no retry, no spam
    }

    [Test]
    public async Task Request_restart_fires_immediately_when_idle() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);   // no Tick needed — evaluates now

        await Assert.That(spy.Calls).IsEqualTo(1);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Restarting);
    }

    [Test]
    public async Task Request_restart_when_busy_queues_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Queued);
        await Assert.That(spy.Calls).IsEqualTo(0);

        busy = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>On Windows only a script install is watched: an update switches its <c>current</c>, so the
    /// binary reached through it changes while the file the daemon runs from does not.</summary>
    [Test]
    public async Task On_Windows_a_script_install_is_watched_through_current() {
        using var tmp = new TempDir();
        tmp.CreateFile("kcap/install.json", "{}");
        var running    = tmp.CreateFile("kcap/versions/1.0.0/bin/kcap-daemon", "old");
        var viaCurrent = tmp.CreateFile("kcap/current/bin/kcap-daemon", "old");

        var before = RestartCoordinator.StatBinaryAt(running, isWindows: true, File.Exists);
        File.WriteAllText(viaCurrent, "the new version");
        var after = RestartCoordinator.StatBinaryAt(running, isWindows: true, File.Exists);

        await Assert.That(before).IsNotNull();
        await Assert.That(RestartDecision.BinaryChanged(before, after)).IsTrue();
        await Assert.That(File.ReadAllText(running)).IsEqualTo("old");
    }

    [Test]
    public async Task On_Windows_a_binary_outside_a_script_install_is_not_watched() {
        using var tmp = new TempDir();
        var exe = tmp.CreateFile("npm/bin/kcap-daemon", "x");

        await Assert.That(RestartCoordinator.StatBinaryAt(exe, isWindows: true, File.Exists)).IsNull();
        await Assert.That(RestartCoordinator.StatBinaryAt(exe, isWindows: false, File.Exists)).IsNotNull();
    }
}
