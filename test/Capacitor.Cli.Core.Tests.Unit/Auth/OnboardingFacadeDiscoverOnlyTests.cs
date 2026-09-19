using Capacitor.Cli.Core.Auth;
using NSubstitute;
using static Capacitor.Tests.Helpers.AuthFixtures;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Pins that <see cref="OnboardingFacade.DiscoverOnlyAsync"/> reports what it found and leaves the
/// config root untouched. Every script here answers the org switch and the token exchange, so a
/// route that reached either would publish — a script that refused them would pass whatever the
/// façade did.
/// </summary>
public class OnboardingFacadeDiscoverOnlyTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string WorkOSProxy = """{"workos_client_id":"client_d"}""";
    const string GitHubProxy = """{"github_client_id":"cid"}""";

    const string SoleWorkOSTenant = """
        [{"provider":"WorkOS","organization_id":"org_a","slug":"eventuous","display_name":"Eventuous","origin":"https://eventuous.kcap.ai"}]
        """;

    const string OrgSwitch = """{"organization_id":"org_a","access_token":"acc2","refresh_token":"rt2"}""";

    string[] WrittenUnderConfigRoot() =>
        Directory.Exists(Config.Directory)
            ? Directory.GetFiles(Config.Directory, "*", SearchOption.AllDirectories)
            : [];

    // The case the separate route exists for: the choosing route selects a sole workspace before any
    // picker is asked, so it cannot be talked out of configuring the machine.
    [Test]
    public async Task A_sole_workos_workspace_is_reported_and_not_selected() {
        using var handler = AuthHttp.Script(proxyConfig: WorkOSProxy, workosTenants: SoleWorkOSTenant, orgSwitch: OrgSwitch);

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler, workosLogin: OnboardingFacadeTests.OrglessAda)
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(report.Error).IsNull();
        await Assert.That(report.Tenants.Select(t => t.Slug)).IsEquivalentTo(new string?[] { "eventuous" });
        await Assert.That(report.CanCreate).IsFalse();
        await Assert.That(handler.Seen.Where(s => s.Contains("/user_management/authenticate"))).IsEmpty();
        await Assert.That(WrittenUnderConfigRoot()).IsEmpty();
    }

    // The same script does publish through the choosing route — otherwise the test above proves nothing.
    [Test]
    public async Task The_same_sole_workspace_is_published_by_the_choosing_route() {
        using var handler = AuthHttp.Script(proxyConfig: WorkOSProxy, workosTenants: SoleWorkOSTenant, orgSwitch: OrgSwitch);

        var result = await NewFacade(Config.Root, new RecordingAuthProgress(), handler, workosLogin: OnboardingFacadeTests.OrglessAda)
            .DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(WrittenUnderConfigRoot()).IsNotEmpty();
    }

    [Test]
    public async Task Several_workos_workspaces_are_all_reported_without_asking_the_picker() {
        using var handler = AuthHttp.Script(
            proxyConfig: WorkOSProxy, workosTenants: OnboardingFacadeTests.WorkOSTenants, orgSwitch: OrgSwitch);

        var picker = Substitute.For<ITenantPicker>();

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler, picker, workosLogin: OnboardingFacadeTests.OrglessAda)
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(report.Tenants.Select(t => t.Slug)).IsEquivalentTo(new string?[] { "eventuous", "contoso" });
        await Assert.That(report.CanCreate).IsFalse();
        await picker.DidNotReceiveWithAnyArgs().PickAsync(default!, default!, default);
        await Assert.That(WrittenUnderConfigRoot()).IsEmpty();
    }

    // An account with none is an answer, not a failure, and a provisioner the façade holds is not
    // consulted: reporting that a workspace can be created must not start creating one.
    [Test]
    public async Task No_workos_workspace_is_an_empty_report_that_offers_nothing() {
        using var handler = AuthHttp.Script(proxyConfig: WorkOSProxy, workosTenants: "[]", orgSwitch: OrgSwitch);

        var provisioner = Substitute.For<ITenantProvisioner>();

        var report = await NewFacade(
                Config.Root, new RecordingAuthProgress(), handler, provisioner: provisioner,
                workosLogin: OnboardingFacadeTests.OrglessAda)
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(report.Error).IsNull();
        await Assert.That(report.Tenants).IsEmpty();
        await Assert.That(report.CanCreate).IsTrue();
        await provisioner.DidNotReceiveWithAnyArgs().OfferCreateAsync(default!, default);
        await Assert.That(WrittenUnderConfigRoot()).IsEmpty();
    }

    [Test]
    public async Task Github_workspaces_are_reported_without_exchanging_a_token() {
        using var handler = AuthHttp.Script(proxyConfig: GitHubProxy, tenants: TwoGitHubTenants);

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler)
            .DiscoverOnlyAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(report.Error).IsNull();
        await Assert.That(report.Tenants.Select(t => t.Origin))
            .IsEquivalentTo(new[] { "https://acme.kcap.ai", "https://contoso.kcap.ai" });
        await Assert.That(report.CanCreate).IsFalse();
        await Assert.That(handler.Seen.Where(s => s.EndsWith("/auth/token", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(WrittenUnderConfigRoot()).IsEmpty();
    }

    // A GitHub-App account gets a workspace by having the app installed on an org, so an empty
    // result there must not say one can be created.
    [Test]
    public async Task No_github_workspace_is_an_empty_report_that_cannot_create() {
        using var handler = AuthHttp.Script(proxyConfig: GitHubProxy, tenants: "[]");

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler)
            .DiscoverOnlyAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(report.Error).IsNull();
        await Assert.That(report.Tenants).IsEmpty();
        await Assert.That(report.CanCreate).IsFalse();
    }

    [Test]
    public async Task An_unreachable_proxy_is_an_error_with_no_rows() {
        using var handler = AuthHttp.Script(); // no /config route

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler)
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(report.Error).IsEqualTo("Cannot reach the Kurrent auth service.");
        await Assert.That(report.Tenants).IsEmpty();
        await Assert.That(report.CanCreate).IsFalse();
    }

    // The proxy client answers a cancelled request as an unreachable one, so without the check a
    // cancel would be reported as an outage.
    [Test]
    public async Task A_cancelled_proxy_call_is_reported_as_cancelled_not_as_an_outage() {
        using var cts     = new CancellationTokenSource();
        using var handler = new AuthHttpScript(_ => throw new OperationCanceledException(cts.Token));
        await cts.CancelAsync();

        var report = await NewFacade(Config.Root, new RecordingAuthProgress(), handler)
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, cts.Token);

        await Assert.That(report.Error).IsEqualTo("Discovery was cancelled.");
    }

    [Test]
    public async Task A_cancel_thrown_by_the_sign_in_is_reported_rather_than_escaping() {
        using var cts     = new CancellationTokenSource();
        using var handler = AuthHttp.Script(proxyConfig: WorkOSProxy, workosTenants: SoleWorkOSTenant);

        var report = await NewFacade(
                Config.Root, new RecordingAuthProgress(), handler,
                workosLogin: ct => {
                    cts.Cancel();

                    throw new OperationCanceledException(ct);
                })
            .DiscoverOnlyAsync(AuthProvider.WorkOS, forceDevice: false, cts.Token);

        await Assert.That(report.Error).IsEqualTo("Discovery was cancelled.");
        await Assert.That(report.Tenants).IsEmpty();
    }
}
