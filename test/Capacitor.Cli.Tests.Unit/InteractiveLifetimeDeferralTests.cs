namespace Capacitor.Cli.Tests.Unit;

[NotInParallel]
public class InteractiveLifetimeDeferralTests {
    static void WithExitRecorder(Action<List<int>> body) {
        var exits    = new List<int>();
        var original = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = exits.Add;

        try {
            body(exits);
        } finally {
            InteractiveLifetime.Exit = original;
        }
    }

    [Test]
    public async Task First_interrupt_cancels_and_returns() {
        var  exits       = new List<int>();
        bool cancelled   = false;
        bool interrupted = false;
        var  original    = InteractiveLifetime.Exit;
        InteractiveLifetime.Exit = exits.Add;

        try {
            var deferral = InteractiveLifetime.Defer();
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            cancelled   = deferral.Token.IsCancellationRequested;
            interrupted = deferral.Interrupted;
            await Assert.That(exits).IsEmpty();

            deferral.Dispose();
        } finally {
            InteractiveLifetime.Exit = original;
        }

        await Assert.That(cancelled).IsTrue();
        await Assert.That(interrupted).IsTrue();
        await Assert.That(exits).IsEquivalentTo([130]);
    }

    [Test]
    public async Task Watchdog_repeats_never_exit_while_deferred() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            using var deferral = InteractiveLifetime.Defer();
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Watchdog);
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Watchdog);
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Watchdog);
            exits.AddRange(e);
            e.Clear();
        });

        await Assert.That(exits).IsEmpty();
    }

    [Test]
    public async Task A_second_signal_exits_130() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            using var deferral = InteractiveLifetime.Defer();
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            exits.AddRange(e);
            e.Clear();
        });

        await Assert.That(exits).IsEquivalentTo([130]);
    }

    [Test]
    public async Task A_signal_after_a_watchdog_interrupt_exits_130() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            using var deferral = InteractiveLifetime.Defer();
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Watchdog);
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            exits.AddRange(e);
            e.Clear();
        });

        await Assert.That(exits).IsEquivalentTo([130]);
    }

    [Test]
    public async Task Dispose_after_interrupt_exits_130() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            var deferral = InteractiveLifetime.Defer();
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            deferral.Dispose();
            deferral.Dispose();
            exits.AddRange(e);
        });

        await Assert.That(exits).IsEquivalentTo([130]);
    }

    [Test]
    public async Task Dispose_without_interrupt_does_not_exit() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            InteractiveLifetime.Defer().Dispose();
            exits.AddRange(e);
        });

        await Assert.That(exits).IsEmpty();
    }

    [Test]
    public async Task Without_a_deferral_an_interrupt_exits_130_as_before() {
        var exits = new List<int>();
        WithExitRecorder(e => {
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Watchdog);
            InteractiveLifetime.Interrupt(InteractiveLifetime.InterruptSource.Signal);
            exits.AddRange(e);
        });

        await Assert.That(exits).IsEquivalentTo([130, 130]);
    }

    [Test]
    public async Task A_nested_deferral_is_refused_and_a_disposed_one_frees_the_slot() {
        using (InteractiveLifetime.Defer()) {
            await Assert.That(InteractiveLifetime.Defer).Throws<InvalidOperationException>();
        }

        InteractiveLifetime.Defer().Dispose();
    }
}
