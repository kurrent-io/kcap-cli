using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Tests.Unit;

// Shares the TokenStoreProfileTests NotInParallel key so the shared KCAP_CONFIG_DIR
// tokens directory isn't raced by other profile-writing tests.
[NotInParallel(nameof(TokenStoreProfileTests))]
public class WorkOSDiscoveryTests {
    static string TokensDir => PathHelpers.ConfigPath("tokens");

    [Before(Test)]
    public void Cleanup() {
        try { if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true); } catch { }
    }

    [Test]
    public async Task RunAsync_org_switches_picked_tenant_and_saves_workos_profile() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d", WorkOSAuthKitDomain = "" };

        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "contoso",   DisplayName = "Contoso",   Origin = "https://contoso.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.Pick(tenants).Returns(tenants[0]); // eventuous

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc",  RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" };

        var exit = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, picker,
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched));

        await Assert.That(exit).IsEqualTo(0);

        var stored = await TokenStore.LoadAsync("eventuous");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.WorkOS);

        var cfg = await AppConfig.LoadProfileConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(cfg.Profiles["eventuous"].ServerUrl).IsEqualTo("https://eventuous.kcap.ai");
    }

    [Test]
    public async Task RunAsync_errors_when_workos_not_configured() {
        var exit = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "" },
            Substitute.For<IAuthProxyClient>(), Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(null),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_errors_when_picked_tenant_has_no_org_id() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" } // no OrganizationId
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var switchCalled = false;
        var exit = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); });

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(switchCalled).IsFalse(); // fail before the org-switch, not during it
    }

    [Test]
    public async Task RunAsync_errors_when_no_tenants() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var exit = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(exit).IsEqualTo(1);
    }
}
