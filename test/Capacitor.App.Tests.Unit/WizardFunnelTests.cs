using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Tests.Unit;

/// <summary>The desktop wizard reports each page it shows, the daemon coming up once, and the page it
/// closed on, so a drop-off can be placed.</summary>
public class WizardFunnelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pages_the_daemon_and_the_close_are_reported() {
        var probe  = TelemetryProbe.Live("setup", Config.Root);
        var daemon = new Subject<(bool, bool)>();

        await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.SignIn), new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;
            using var funnel = WizardFunnel.Attach(vm, probe.Funnel, daemon);

            daemon.OnNext((false, false));
            daemon.OnNext((true, false));
            daemon.OnNext((true, false));
            await vm.NextCommand.Execute().ToTask();
            await vm.PendingAdvanceForTesting;
            await vm.NextCommand.Execute().ToTask();
        });

        await Assert.That(probe.Names).IsEquivalentTo(
            ["cli_setup_step_shown", "cli_setup_daemon_enabled", "cli_setup_step_shown", "cli_setup_wizard_closed"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(probe.Captured("cli_setup_step_shown").Select(e => e.Properties["step"]!.GetValue<string>()))
            .IsEquivalentTo(["signin", "done"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var closed = probe.Only("cli_setup_wizard_closed");
        await Assert.That(closed.Properties["step"]!.GetValue<string>()).IsEqualTo("done");
        await Assert.That(closed.Properties["finished"]!.GetValue<bool>()).IsTrue();
        await Assert.That(probe.Only("cli_setup_daemon_enabled").Properties["already_running"]!.GetValue<bool>()).IsFalse();
    }

    /// <summary>The enabler can finish after the wizard closes; that still counts, and a daemon that was
    /// already running says so.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_daemon_enabled_after_the_close_is_still_reported(bool alreadyRunning) {
        var probe  = TelemetryProbe.Live("setup", Config.Root);
        var daemon = new Subject<(bool, bool)>();

        await AvaloniaSession.DispatchAsync(async () => {
            var vm = new OnboardingViewModel([new FakeWizardStep(WizardStepId.Done)]);
            await vm.PendingEnterForTesting;
            var funnel = WizardFunnel.Attach(vm, probe.Funnel, daemon);
            await vm.NextCommand.Execute().ToTask();
            funnel.Dispose();

            daemon.OnNext((true, alreadyRunning));
        });

        await Assert.That(probe.Only("cli_setup_daemon_enabled").Properties["already_running"]!.GetValue<bool>()).IsEqualTo(alreadyRunning);
    }
}
