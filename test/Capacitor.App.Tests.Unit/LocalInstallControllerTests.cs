using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class LocalInstallControllerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static LocalInstallController Controller(FakeKcapCli cli, LocalRoot root, TelemetryProbe probe, bool committedLocal = false) =>
        new(() => cli, root, () => committedLocal, action => action(), probe.Funnel);

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    [Test]
    public async Task Cancel_then_settle_sends_one_interrupt() {
        var signals = 0;
        var cli = new FakeKcapCli {
            SetupLocalBehavior = async (onLine, interrupt, _) => {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var reg = interrupt.Register(() => {
                    signals++;
                    exited.TrySetResult();
                });
                await exited.Task;
                onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("cancelled")));
                return new StreamingResult(130, false, []);
            },
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(CancellationToken.None);
        controller.Interrupt();
        controller.Interrupt();
        await controller.SettleAsync();

        await Assert.That(signals).IsEqualTo(1);
        await Assert.That(controller.Running).IsFalse();
        await Assert.That(controller.Outcome).IsTypeOf<LocalInstallOutcome.Failed>();
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }

    [Test]
    public async Task Settle_when_idle_completes_at_once_and_start_while_running_spawns_nothing() {
        var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => exit.Task };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.SettleAsync();
        await controller.StartAsync(CancellationToken.None);
        await controller.StartAsync(CancellationToken.None);
        exit.SetResult(new StreamingResult(0, false, []));
        await controller.SettleAsync();

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task No_cli_settles_retryable() {
        var cli = new FakeKcapCli {
            CliPath = null,
            SetupLocalBehavior = (_, _, _) => Task.FromResult(
                new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")])),
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Retry);
        await Assert.That(failed.Message).IsEqualTo("kcap CLI not found");
        await Assert.That(failed.LockedToLocal).IsFalse();
    }

    [Test]
    public async Task A_throwing_run_settles_retryable_instead_of_hanging() {
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => throw new InvalidOperationException("spawn refused") };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Retry);
        await Assert.That(failed.Message).Contains("spawn refused");
    }

    [Test]
    public async Task Started_on_spawn_and_committed_once_with_its_plan() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), probe);
        LocalInstallOutcome? settled = null;
        controller.Settled += o => settled = o;

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(settled).IsEqualTo(controller.Outcome);
        await Assert.That(((LocalInstallOutcome.Installed)controller.Outcome!).ServerUrl).IsEqualTo(LocalSetupScript.ServerUrl);
        await Assert.That(probe.Names).IsEquivalentTo(
            ["cli_setup_local_install_started", "cli_setup_local_install_committed"], CollectionOrdering.Matching);
        await Assert.That(probe.Only("cli_setup_local_install_committed").Properties["plan"]!.GetValue<string>()).IsEqualTo("fresh");
    }

    [Test]
    public async Task A_busy_lock_under_a_pending_setup_settles_locked_and_says_so() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("lifecycle_busy")) };
        var controller = Controller(cli, LocalSetupScript.PendingSetup(Config), probe);

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Busy);
        await Assert.That(failed.LockedToLocal).IsTrue();
        var e = probe.Only("cli_setup_local_install_failed");
        await Assert.That(e.Properties["code"]!.GetValue<string>()).IsEqualTo("lifecycle_busy");
        await Assert.That(e.Properties["locked"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task A_committed_local_profile_locks_a_failure_without_a_journal() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe(), committedLocal: true);

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).LockedToLocal).IsTrue();
    }

    [Test]
    public async Task A_new_run_starts_from_fresh_progress() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(CancellationToken.None);
        await controller.SettleAsync();
        var first = controller.Progress;

        cli.SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed);
        await controller.RetryAsync(CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(controller.Progress).IsNotSameReferenceAs(first);
        await Assert.That(controller.Progress.FailedCode).IsNull();
        await Assert.That(controller.Outcome).IsTypeOf<LocalInstallOutcome.Installed>();
    }
}
