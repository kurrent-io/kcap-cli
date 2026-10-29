using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class LocalUpgradeControllerTests {
    static LocalUpgradeController Controller(FakeKcapCli cli) => new(() => cli, action => action());

    [Test]
    public async Task A_finished_run_settles_once_after_every_line() {
        var cli = new FakeKcapCli {
            LocalUpgradeBehavior = UpgradeScript.Prints(0, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Upgraded("1.2.0", "1.3.0")),
        };
        var controller = Controller(cli);
        var settled = new List<LocalUpgradeOutcome>();
        string? toWhenSettled = null;
        controller.Settled += outcome => {
            settled.Add(outcome);
            toWhenSettled = controller.Progress.To;
        };

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(settled.Count).IsEqualTo(1);
        await Assert.That(settled[0]).IsEqualTo(new LocalUpgradeOutcome.Upgraded("1.2.0", "1.3.0"));
        await Assert.That(toWhenSettled).IsEqualTo("1.3.0");
        await Assert.That(controller.Running).IsFalse();
        await Assert.That(controller.Outcome).IsEqualTo(new LocalUpgradeOutcome.Upgraded("1.2.0", "1.3.0"));
    }

    /// Every interrupt request lands on the one token the child was started with, and the settle
    /// waits for the child's own exit rather than completing on the request.
    [Test]
    public async Task Cancel_then_settle_sends_one_interrupt() {
        var signals = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli {
            LocalUpgradeBehavior = async (onLine, interrupt, _) => {
                var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (interrupt.Register(() => {
                    Interlocked.Increment(ref signals);
                    interrupted.TrySetResult();
                })) {
                    await interrupted.Task;
                }
                await release.Task;
                onLine(new StreamedLine(ProcessStreamKind.Stdout, UpgradeScript.Failed("cancelled", "The upgrade was cancelled.", false)));
                return new StreamingResult(1, false, []);
            },
        };
        var controller = Controller(cli);

        await controller.StartAsync(CancellationToken.None);
        controller.Interrupt();
        controller.Interrupt();
        var settle = controller.SettleAsync();
        await Task.Delay(50);

        await Assert.That(settle.IsCompleted).IsFalse();
        await Assert.That(controller.Running).IsTrue();

        release.SetResult();
        await settle.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(signals).IsEqualTo(1);
        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(1);
        await Assert.That(((LocalUpgradeOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }

    /// A runner that reports on SIGINT and tree-kills on its cancellation token, as ProcessRunner does.
    sealed class InterruptibleRunner : IProcessRunner {
        public int Interrupts;
        public int Kills;
        public readonly TaskCompletionSource Spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, "", "", false));

        public async Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) {
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var onInterrupt = options.Interrupt.Register(() => {
                Interlocked.Increment(ref Interrupts);
                stopped.TrySetResult(false);
            });
            await using var onKill = ct.Register(() => {
                Interlocked.Increment(ref Kills);
                stopped.TrySetResult(true);
            });
            Spawned.TrySetResult();

            if (await stopped.Task) return new StreamingResult(137, false, []);

            onLine(new StreamedLine(ProcessStreamKind.Stdout, UpgradeScript.Failed("cancelled", "The upgrade was cancelled.", false)));
            return new StreamingResult(1, false, []);
        }
    }

    [Test]
    public async Task A_quit_interrupts_the_running_upgrade_once_and_never_kills_it() {
        var runner = new InterruptibleRunner();
        var cli = new KcapCli(runner, "kcap", "daemon-a", "default", terminalPathAsync: null);
        var controller = new LocalUpgradeController(() => cli, action => action());
        using var shutdown = new CancellationTokenSource();

        await controller.StartAsync(shutdown.Token);
        await runner.Spawned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.CancelAsync();
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(runner.Interrupts).IsEqualTo(1);
        await Assert.That(runner.Kills).IsEqualTo(0);
        await Assert.That(((LocalUpgradeOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }

    [Test]
    public async Task Start_while_running_spawns_nothing() {
        var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli { LocalUpgradeBehavior = (_, _, _) => exit.Task };
        var controller = Controller(cli);

        await controller.StartAsync(CancellationToken.None);
        await controller.StartAsync(CancellationToken.None);
        exit.SetResult(new StreamingResult(3, false, []));
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_run_with_no_cli_settles_failed_with_its_reason() {
        var cli = new FakeKcapCli {
            LocalUpgradeBehavior = (_, _, _) => Task.FromResult(
                new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")])),
        };
        var controller = Controller(cli);

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(((LocalUpgradeOutcome.Failed)controller.Outcome!).Message).IsEqualTo("kcap CLI not found");
    }

    [Test]
    public async Task A_spawn_that_throws_settles_failed() {
        var cli = new FakeKcapCli { LocalUpgradeBehavior = (_, _, _) => throw new InvalidOperationException("no fork") };
        var controller = Controller(cli);

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(((LocalUpgradeOutcome.Failed)controller.Outcome!).Message).IsEqualTo("no fork");
    }

    [Test]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, true)]
    public async Task Only_an_update_relaunch_on_a_committed_local_machine_with_no_pending_setup_upgrades(
            bool updateRelaunch, bool committedLocal, bool setupPending) {
        var cli = new FakeKcapCli();
        var controller = Controller(cli);

        var started = await controller.StartAfterUpdateAsync(updateRelaunch, committedLocal, () => setupPending, CancellationToken.None);
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(started).IsFalse();
        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(0);
    }

    /// A quit that lands while the journal is read must not leave a child spawned after the quit's
    /// settle found nothing to wait for.
    [Test]
    public async Task A_shutdown_during_the_journal_read_spawns_nothing() {
        var cli = new FakeKcapCli();
        var controller = Controller(cli);
        using var shutdown = new CancellationTokenSource();

        var started = await controller.StartAfterUpdateAsync(true, true, () => {
            shutdown.Cancel();
            return false;
        }, shutdown.Token);

        await Assert.That(started).IsFalse();
        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(0);
        await Assert.That(controller.Running).IsFalse();
    }

    [Test]
    public async Task Start_after_shutdown_spawns_nothing() {
        var cli = new FakeKcapCli();
        var controller = Controller(cli);

        await controller.StartAsync(new CancellationToken(canceled: true));

        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(0);
        await Assert.That(controller.Running).IsFalse();
    }

    [Test]
    public async Task An_update_relaunch_on_a_committed_local_machine_upgrades() {
        var cli = new FakeKcapCli();
        var controller = Controller(cli);

        var started = await controller.StartAfterUpdateAsync(true, true, () => false, CancellationToken.None);
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(started).IsTrue();
        await Assert.That(cli.LocalUpgradeCallCount).IsEqualTo(1);
    }

    /// A start that another change to the installation holds off is not a start: nothing will settle, so a caller
    /// waiting on the outcome must hear that instead.
    [Test]
    public async Task A_held_upgrade_after_an_update_reports_that_it_did_not_start() {
        var cli = new FakeKcapCli { LocalUpgradeBehavior = UpgradeScript.Prints(0, UpgradeScript.Upgraded("1.2.0", "1.3.0")) };
        var controller = new LocalUpgradeController(() => cli, action => action(), held: () => true);

        var started = await controller.StartAfterUpdateAsync(true, true, () => false, CancellationToken.None);

        await Assert.That(started).IsFalse();
        await Assert.That(controller.Running).IsFalse();
    }
}
