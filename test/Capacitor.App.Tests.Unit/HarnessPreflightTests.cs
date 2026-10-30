using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class HarnessPreflightTests {
    static Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> Detecting(
            params (HarnessId Id, DetectedAgent Agent)[] found) =>
        _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(found.ToDictionary(f => f.Id, f => f.Agent));

    static OnboardingViewModel Wizard(HarnessPreflight preflight) =>
        new([new FakeWizardStep(WizardStepId.Edition), new FakeWizardStep(WizardStepId.Done)], preflight: preflight);

    [Test]
    public async Task No_harness_stops_setup_before_its_first_step() {
        var (blocked, stepVisible, footer, canNext) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;

            return (vm.PreflightBlocked, vm.StepVisible, vm.FooterVisible, vm.CanGoNext);
        });

        await Assert.That(blocked).IsTrue();
        await Assert.That(stepVisible).IsFalse();
        await Assert.That(footer).IsFalse();
        await Assert.That(canNext).IsFalse();
    }

    /// The Claude and Codex desktop apps keep their CLIs off PATH; the app alone is a harness.
    [Test]
    public async Task A_desktop_app_alone_passes() {
        var (blocked, stepVisible) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting((HarnessId.Codex, new DetectedAgent(false, false, DesktopAppFound: true)))));
            await vm.PendingPreflightForTesting;

            return (vm.PreflightBlocked, vm.StepVisible);
        });

        await Assert.That(blocked).IsFalse();
        await Assert.That(stepVisible).IsTrue();
    }

    [Test]
    public async Task A_failed_detection_lets_setup_continue() {
        var state = await AvaloniaSession.DispatchAsync(async () => {
            var preflight = new HarnessPreflight(_ => throw new IOException("login shell went away"));
            var vm = Wizard(preflight);
            await vm.PendingPreflightForTesting;

            return preflight.State;
        });

        await Assert.That(state).IsEqualTo(HarnessPreflightState.Passed);
    }

    [Test]
    public async Task The_wizard_waits_while_detection_runs() {
        var (checkingBefore, canNextBefore, stepVisibleAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<IReadOnlyDictionary<HarnessId, DetectedAgent>>();
            var vm = Wizard(new HarnessPreflight(_ => gate.Task));
            var before = (vm.PreflightChecking, vm.CanGoNext);
            gate.SetResult(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Claude] = new(true, false) });
            await vm.PendingPreflightForTesting;

            return (before.PreflightChecking, before.CanGoNext, vm.StepVisible);
        });

        await Assert.That(checkingBefore).IsTrue();
        await Assert.That(canNextBefore).IsFalse();
        await Assert.That(stepVisibleAfter).IsTrue();
    }

    /// Quit leaves the app even where setup could otherwise be deferred and the window is still up.
    [Test]
    public async Task Quit_closes_setup_and_the_app() {
        var (closed, quits) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;
            await vm.QuitCommand.Execute().ToTask();

            return (vm.Closed, AppUnderTest.QuitsAfterWizardClose(vm, windowClosed: false));
        });

        await Assert.That(closed).IsTrue();
        await Assert.That(quits).IsTrue();
    }
}
