using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

/// A fresh app holds usage data until the wizard's edition is known. The cloud edition then sends what was held and
/// shows its notice; the local edition keeps holding it for the Privacy page's answer.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionUsageDataTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe FreshDesktop() =>
        TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl, UnsavedConsent.Pending), Config.Root);

    static WizardGraphOptions Wired(WizardFixtures.GraphHarness harness, TelemetryProbe probe, UsageDataNotice notice) =>
        harness.Options() with {
            Bridges     = WizardComposition.BuildBridges(action => action(), probe.Telemetry, AuthEndpoints.Defaults),
            UsageNotice = notice,
        };

    [Test]
    [Arguments(EditionChoice.Cloud)]
    [Arguments(EditionChoice.ExistingWorkspace)]
    public async Task A_cloud_choice_sends_what_waited_and_turns_the_privacy_page_into_the_notice(EditionChoice choice) {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var probe  = FreshDesktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);

        var (applicable, askingBefore, askingAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(Wired(harness, probe, notice));
            await graph.ViewModel.PendingEnterForTesting;
            var privacy = graph.Steps.OfType<UsageDataStepViewModel>().Single();
            var asking  = privacy.Asking;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();

            await (choice == EditionChoice.Cloud ? edition.ChooseCloudCommand : edition.SignInCommand).Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;

            return (privacy.Applicable, asking, privacy.Asking);
        });

        await Assert.That(applicable).IsTrue();
        await Assert.That(askingBefore).IsTrue();
        await Assert.That(askingAfter).IsFalse();
        await Assert.That(notice.Visible).IsTrue();
        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Sink.Flushes).IsGreaterThan(0);
        await Assert.That(probe.Names).Contains("cli_setup_edition_offered");
    }

    /// Nothing reaches the sink while the edition screen is up: the edition decides whether anything may.
    [Test]
    public async Task The_edition_screen_sends_nothing_before_a_choice() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var probe  = FreshDesktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(Wired(harness, probe, notice));
            await graph.ViewModel.PendingEnterForTesting;

            return true;
        });

        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Sink.Flushes).IsEqualTo(0);
        await Assert.That(probe.Names).Contains("cli_setup_edition_offered");
        await Assert.That(probe.Telemetry.AwaitingChoice).IsTrue();
        await Assert.That(notice.Visible).IsFalse();
    }

    /// Someone back from kcap.ai's sign-up starts on Sign-in: the cloud edition, chosen in an earlier run.
    [Test]
    public async Task A_resumed_web_sign_up_is_the_cloud_edition() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var probe  = FreshDesktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);

        await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(Wired(harness, probe, notice) with { ResumeWebSignUp = true });
            await graph.ViewModel.PendingEnterForTesting;

            return true;
        });

        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(notice.Visible).IsTrue();
    }
}
