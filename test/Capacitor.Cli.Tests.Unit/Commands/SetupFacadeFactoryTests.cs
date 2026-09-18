using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using NSubstitute;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The two arguments the façade factory is handed that nothing downstream reports: the picker the
/// run chose and the workspace it asked for. Every other setup test substitutes the factory, so this
/// mapping is the one thing they cannot pin — drop either argument and they all still pass.
/// </summary>
// Bare: the façade's progress sink writes through the AnsiConsole singleton, which is process-global.
[NotInParallel]
public class SetupFacadeFactoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    IOnboardingFacadeFactory Factory(HttpMessageHandler handler) {
        var http = new PlainHttpClientFactory(handler);

        return new SetupFacadeFactory(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), http,
            new AuthProxyClient(http.CreateClient(CapacitorClients.Anonymous), TimeProvider.System),
            new GitHubOAuthClient(http), new WorkOSClient(http, TimeProvider.System), new RecordingBrowser(),
            NoTelemetry.Facade, AuthEndpoints.Defaults, TimeProvider.System);
    }

    static AuthHttpScript TwoTenants() =>
        AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""", tenants: AuthFixtures.TwoGitHubTenants);

    [Test]
    public async Task Discovery_consults_the_picker_it_was_given() {
        using var handler = TwoTenants();
        var       picker  = AuthFixtures.PickerReturningFirst();
        using var capture = ConsoleOutput.StartCapture();

        await Factory(handler).Create(provisioner: null, picker)
            .DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await picker.Received().PickAsync(
            Arg.Any<DiscoveredTenant[]>(), Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The requested workspace reaches the façade only as the commit guard built from it, so a run
    /// that lands somewhere else is the one observable proof it was carried.
    /// </summary>
    [Test]
    public async Task A_requested_workspace_refuses_a_commit_that_would_publish_another() {
        using var handler = TwoTenants();
        using var capture = ConsoleOutput.StartCapture();

        var result = await Factory(handler)
            .Create(provisioner: null, AuthFixtures.PickerReturningFirst(), new RequestedWorkspace("Requested", "requested"))
            .DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsNotTypeOf<AuthResult.Committed>();
    }
}
