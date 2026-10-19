using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The cloud choice through the REAL composition: kcap-web's sign-up opens in the browser, Sign-in
/// waits on it, and a wait that runs out returns to the edition screen saying so.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionCloudSignUpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task The_cloud_choice_opens_the_desktop_sign_up_and_waits_on_sign_in() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var (current, phase) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            await graph.Steps.OfType<EditionStepViewModel>().Single().ChooseCloudCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;

            return (graph.ViewModel.Current.Id, graph.Steps.OfType<SignInStepViewModel>().Single().Phase);
        });

        await Assert.That(current).IsEqualTo(WizardStepId.SignIn);
        await Assert.That(phase).IsEqualTo(SignInPhase.SignUp);
        await Assert.That(harness.UrlOpener.Opened).IsEquivalentTo(
            [AuthEndpoints.DefaultSignupUrl + "/get-started?tier=team&via=desktop"]);
    }

    [Test]
    public async Task Signing_in_to_an_existing_workspace_opens_no_sign_up() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var phase = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            await graph.Steps.OfType<EditionStepViewModel>().Single().SignInCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;

            return graph.Steps.OfType<SignInStepViewModel>().Single().Phase;
        });

        await Assert.That(phase).IsEqualTo(SignInPhase.Start);
        await Assert.That(harness.UrlOpener.Opened).IsEmpty();
    }

    [Test]
    public async Task An_expired_wait_returns_to_the_edition_screen_with_a_message() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var time = new FakeTimeProvider();
        harness.Time = time;

        var (current, notice) = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();

            await edition.ChooseCloudCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;
            time.Advance(SignInStepViewModel.SignUpWait);
            await WizardFixtures.WaitUntilAsync(
                () => graph.ViewModel.Current.Id == WizardStepId.Edition, what: "the expired wait to return to the edition");

            return (graph.ViewModel.Current.Id, edition.Notice);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(current).IsEqualTo(WizardStepId.Edition);
        await Assert.That(notice).IsEqualTo(SignInStepViewModel.SignUpExpiredNotice);
    }
}
