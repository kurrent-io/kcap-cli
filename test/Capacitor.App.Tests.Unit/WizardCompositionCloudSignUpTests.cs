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

    [Test]
    public async Task Closing_the_wizard_ends_the_sign_up_wait() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var time = new FakeTimeProvider();
        harness.Time = time;

        var notice = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();
            await edition.ChooseCloudCommand.Execute().ToTask();
            await graph.ViewModel.PendingAdvanceForTesting;

            graph.ViewModel.RequestClose();
            time.Advance(SignInStepViewModel.SignUpWait * 2);
            await Task.Delay(100);

            return edition.Notice;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(notice).IsNull();
    }

    /// kcap-web adds the owner to the workspace's organization before the workspace is live, so
    /// discovery can list one still provisioning; its host 404s until then and is left out.
    [Test]
    public async Task The_wizard_facade_probes_a_discovered_workspace_before_signing_in_to_it() {
        using var handler = new StatusHandler(System.Net.HttpStatusCode.NotFound);
        var bridges = WizardComposition.BuildBridges(action => action(), Capacitor.Cli.Core.Telemetry.CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults);
        WizardFacadeSpec? spec = null;

        WizardComposition.BuildOperation(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), new PlainHttpClientFactory(handler),
            new AuthProxyClient(new HttpClient(handler, disposeHandler: false), TimeProvider.System),
            new GitHubOAuthClient(new PlainHttpClientFactory()), new WorkOSClient(new PlainHttpClientFactory(), TimeProvider.System),
            "default", bridges, new ConsentFlipClaims(Config.Root), TimeProvider.System,
            s => {
                spec = s;
                return (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());
            });

        var answer = await spec!.ProbeWorkspace(
            new DiscoveredTenant { Origin = "https://acme.kcap.ai" }, "bearer", CancellationToken.None);

        await Assert.That(answer).IsEqualTo(WorkspaceAnswer.Gone);
        await Assert.That(handler.Requests).IsEquivalentTo(["https://acme.kcap.ai/auth/config"]);
    }

    sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
