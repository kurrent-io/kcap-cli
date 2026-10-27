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

    /// A discovered workspace kcap.ai is still creating is waited on, saying so, until its host answers.
    /// One kcap.ai calls failed, does not know, or made live without an organization is gone, and one it can
    /// no longer be asked about is pending.
    [Test]
    [Arguments(new[] { "provisioning" }, WorkspaceAnswer.Live, true, false)]
    [Arguments(new[] { "provisioning", "failed" }, WorkspaceAnswer.Gone, true, true)]
    [Arguments(new[] { "provisioning", "active" }, WorkspaceAnswer.Gone, true, true)]
    [Arguments(new[] { "provisioning", "401" }, WorkspaceAnswer.Pending, true, false)]
    [Arguments(new string[0], WorkspaceAnswer.Gone, false, false)]
    [Arguments(new[] { "failed" }, WorkspaceAnswer.Gone, false, true)]
    public async Task A_workspace_still_being_created_is_waited_on_until_it_answers(
            string[] states, WorkspaceAnswer expected, bool waited, bool failed) {
        var time = new FakeTimeProvider();
        using var handler = new ProvisioningHandler(states);
        var waitedFor = new List<string>();
        var failedFor = new List<string>();
        var probe = WizardComposition.ProbeWorkspace(
            new PlainHttpClientFactory(handler), AuthEndpoints.Defaults, waitedFor.Add, failedFor.Add, time);

        var answer = probe(new DiscoveredTenant { Origin = "https://acme.kcap.ai", Slug = "acme", DisplayName = "Acme" }, "bearer", CancellationToken.None);
        for (var tick = 0; tick < 50 && !answer.IsCompleted; tick++) {
            time.Advance(WizardComposition.ProvisioningPollInterval);
            await Task.Delay(20);
        }

        await Assert.That(await answer.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(expected);
        await Assert.That(waitedFor.Count > 0).IsEqualTo(waited);
        await Assert.That(failedFor.Count > 0).IsEqualTo(failed);
    }

    /// A live workspace found in the same discovery ends the wait on one still being created, so the
    /// live one is offered without waiting for the other; one that did not answer does not.
    [Test]
    [Arguments(System.Net.HttpStatusCode.OK, WorkspaceAnswer.Live, WorkspaceAnswer.Pending, 1)]
    [Arguments(System.Net.HttpStatusCode.ServiceUnavailable, WorkspaceAnswer.NoAnswer, WorkspaceAnswer.Live, 2)]
    public async Task A_live_workspace_ends_the_wait_on_one_still_being_created(
            System.Net.HttpStatusCode sibling, WorkspaceAnswer siblingAnswer, WorkspaceAnswer expected, int statusPolls) {
        var time = new FakeTimeProvider();
        using var handler = new ProvisioningHandler(["provisioning"], siblingOrigin: "https://older.kcap.ai", siblingStatus: sibling);
        var probe = WizardComposition.ProbeWorkspace(new PlainHttpClientFactory(handler), AuthEndpoints.Defaults, _ => { }, _ => { }, time);

        var pending = probe(new DiscoveredTenant { Origin = "https://acme.kcap.ai", Slug = "acme" }, "bearer", CancellationToken.None);
        var other   = await probe(new DiscoveredTenant { Origin = "https://older.kcap.ai", Slug = "older" }, "bearer", CancellationToken.None);
        for (var tick = 0; tick < 50 && !pending.IsCompleted; tick++) {
            time.Advance(WizardComposition.ProvisioningPollInterval);
            await Task.Delay(20);
        }

        await Assert.That(other).IsEqualTo(siblingAnswer);
        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(expected);
        await Assert.That(handler.StatusPolls).IsEqualTo(statusPolls);
    }

    /// A live workspace already found means the one still being created is left out at once, with no wait.
    [Test]
    public async Task A_live_workspace_found_first_leaves_one_still_being_created_out_at_once() {
        using var handler = new ProvisioningHandler(["provisioning"], siblingOrigin: "https://older.kcap.ai");
        var probe = WizardComposition.ProbeWorkspace(
            new PlainHttpClientFactory(handler), AuthEndpoints.Defaults, _ => { }, _ => { }, new FakeTimeProvider());

        var other   = await probe(new DiscoveredTenant { Origin = "https://older.kcap.ai", Slug = "older" }, "bearer", CancellationToken.None);
        var pending = await probe(new DiscoveredTenant { Origin = "https://acme.kcap.ai", Slug = "acme" }, "bearer", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(other).IsEqualTo(WorkspaceAnswer.Live);
        await Assert.That(pending).IsEqualTo(WorkspaceAnswer.Pending);
    }

    /// Of two workspaces still being created, the first to go live ends the wait on the other.
    [Test]
    public async Task A_workspace_that_goes_live_during_the_wait_ends_the_wait_on_another() {
        var time = new FakeTimeProvider();
        using var first  = new ProvisioningHandler(["provisioning"]);
        using var second = new ProvisioningHandler(["provisioning", "provisioning", "provisioning", "provisioning", "provisioning", "provisioning", "provisioning", "provisioning"], neverLive: true);
        var probe = WizardComposition.ProbeWorkspace(new RoutingHttpClientFactory(first, second), AuthEndpoints.Defaults, _ => { }, _ => { }, time);

        var goesLive = probe(new DiscoveredTenant { Origin = "https://acme.kcap.ai", Slug = "acme" }, "bearer", CancellationToken.None);
        var stays    = probe(new DiscoveredTenant { Origin = "https://slow.kcap.ai", Slug = "slow" }, "bearer", CancellationToken.None);
        for (var tick = 0; tick < 50 && !(goesLive.IsCompleted && stays.IsCompleted); tick++) {
            time.Advance(WizardComposition.ProvisioningPollInterval);
            await Task.Delay(20);
        }

        await Assert.That(await goesLive.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(WorkspaceAnswer.Live);
        await Assert.That(await stays.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(WorkspaceAnswer.Pending);
        await Assert.That(second.StatusPolls < 8).IsTrue();
    }

    /// Sends a request for the slow workspace's host or slug to the second handler, the rest to the first.
    sealed class RoutingHttpClientFactory(HttpMessageHandler first, HttpMessageHandler second) : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new(new Router(first, second), disposeHandler: true);

        sealed class Router(HttpMessageHandler first, HttpMessageHandler second) : DelegatingHandler {
            readonly HttpMessageInvoker _first = new(first, disposeHandler: false), _second = new(second, disposeHandler: false);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                request.RequestUri!.ToString().Contains("slow", StringComparison.Ordinal)
                    ? _second.SendAsync(request, ct)
                    : _first.SendAsync(request, ct);
        }
    }

    /// Someone coming back from kcap.ai's sign-up opens on sign-in, without the sign-up page again;
    /// everyone else starts on the edition choice.
    [Test]
    [Arguments(true, WizardStepId.SignIn)]
    [Arguments(false, WizardStepId.Edition)]
    public async Task A_relaunch_after_the_web_sign_up_opens_on_sign_in(bool resume, WizardStepId expected) {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);

        var current = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(harness.Options() with { ResumeWebSignUp = resume });
            await graph.ViewModel.PendingEnterForTesting;

            return graph.ViewModel.Current.Id;
        });

        await Assert.That(current).IsEqualTo(expected);
        await Assert.That(harness.UrlOpener.Opened).IsEmpty();
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(23, true)]
    [Arguments(25, false)]
    [Arguments(-1, false)]
    public async Task A_web_sign_up_is_resumed_only_within_a_day(int hoursAgo, bool expected) {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var state = new Capacitor.App.Services.AppState(WebSignUpOpenedAt: now - TimeSpan.FromHours(hoursAgo));

        await Assert.That(WizardComposition.ResumesWebSignUp(state, now)).IsEqualTo(expected);
        await Assert.That(WizardComposition.ResumesWebSignUp(new Capacitor.App.Services.AppState(), now)).IsFalse();
    }

    /// The host 404s until it is live, after two status polls of a workspace that only provisions; kcap-web's
    /// status answers each poll with the next scripted state, holding the last ("401" is an expired bearer),
    /// and 404s when none is scripted. A sibling origin answers its own status throughout; a never-live host 404s throughout.
    sealed class ProvisioningHandler(
            string[] states, string? siblingOrigin = null, System.Net.HttpStatusCode siblingStatus = System.Net.HttpStatusCode.OK,
            bool neverLive = false)
        : HttpMessageHandler {
        int _polls;

        public int StatusPolls => _polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;
            if (siblingOrigin is not null && request.RequestUri.GetLeftPart(UriPartial.Authority) == siblingOrigin)
                return Task.FromResult(new HttpResponseMessage(siblingStatus));
            if (path == "/auth/config") {
                var live = !neverLive && states is ["provisioning"] && _polls >= 2;
                return Task.FromResult(new HttpResponseMessage(live ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound));
            }

            if (states.Length == 0) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            var state = states[Math.Min(_polls++, states.Length - 1)];
            if (state == "401") return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent($"{{\"state\":\"{state}\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
