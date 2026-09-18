using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Duende.IdentityModel.OidcClient.Browser;
using NSubstitute;

namespace Capacitor.Tests.Helpers;

// Shared by the Core façade tests and the CLI login/setup parity tests, which sit in different
// assemblies and so cannot reach a fixture declared in either one.
public static class AuthFixtures {
    /// <summary>
    /// A real <see cref="TokenStore"/> over unconfigured clients — the one construction site for a
    /// constructor the suites reach from eighty-odd files. A test that scripts what the refresh lanes
    /// answer passes a handler.
    /// </summary>
    /// <param name="env">The environment overrides the refresh lanes resolve a server through. Pass
    /// one only when the test is about that resolution.</param>
    /// <param name="time">The clock the store and its refresh lanes share. Pass a fake only when the
    /// test asserts on expiry or on a refresh deadline.</param>
    public static TokenStore NewTokenStore(
            ConfigRoot root, HttpMessageHandler? handler = null, ProfileOverrides? env = null,
            TimeProvider? time = null) {
        var factory = new PlainHttpClientFactory(handler);
        var clock   = time ?? TimeProvider.System;

        return new TokenStore(root, env ?? ProfileOverrides.None, factory, new WorkOSClient(factory, clock), clock);
    }

    public static OnboardingFacade NewFacade(
            ConfigRoot                                                  root,
            IAuthProgress                                               progress,
            HttpMessageHandler                                          handler,
            ITenantPicker?                                              picker        = null,
            ITenantProvisioner?                                         provisioner   = null,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit  = null,
            Func<CancellationToken, Task<WorkOSAuthResponse?>>?         workosLogin   = null,
            IBrowser?                                                   workosBrowser = null,
            string?                                                     workosApiBase = null,
            IBrowserLauncher?                                           browser       = null,
            // A facade that is off unless a test hands one in, like every other collaborator
            // defaulted here: a test that does not observe telemetry must not have to name it, and
            // one that does gets an empty sink rather than a silent pass if it forgets.
            CliTelemetry?                                               telemetry     = null,
            TimeProvider?                                               time          = null) {
        var factory = new PlainHttpClientFactory(handler);
        var clock   = time ?? TimeProvider.System;

        return new OnboardingFacade(
                root, NewTokenStore(root, time: clock), factory,
                new AuthProxyClient(factory.CreateClient(CapacitorClients.Anonymous), clock),
                new GitHubOAuthClient(factory), new WorkOSClient(factory, clock),
                progress, browser ?? new RecordingBrowser(),
                picker ?? Substitute.For<ITenantPicker>(), provisioner,
                telemetry ?? CliTelemetry.Disabled(clock), AuthEndpoints.Defaults, clock, beforeCommit) {
            WorkOSOrglessLogin    = workosLogin,
            WorkOSBrowser         = workosBrowser,
            WorkOSApiBaseOverride = workosApiBase
        };
    }

    public static ITenantPicker PickerReturningFirst() {
        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>())
              .Returns(ci => Task.FromResult<DiscoveredTenant?>(ci.Arg<DiscoveredTenant[]>()[0]));

        return picker;
    }

    public const string TwoGitHubTenants = """
        [{"org_id":1,"org_login":"acme","origin":"https://acme.kcap.ai"},
         {"org_id":2,"org_login":"contoso","origin":"https://contoso.kcap.ai"}]
        """;
}
