using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using NSubstitute;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class TenantDiscoveryTests {
    [Test]
    public async Task RunAsync_auto_picks_single_tenant() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(
                 [new DiscoveredTenant { OrgId = 1, OrgLogin = "solo", Origin = "https://solo.example" }],
                 DiscoveryError.None)));

        var picker    = Substitute.For<ITenantPicker>();
        var discovery = new TenantDiscovery(proxy, picker);

        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNotNull();
        await Assert.That(outcome.Picked!.OrgLogin).IsEqualTo("solo");
        picker.DidNotReceive().Pick(Arg.Any<DiscoveredTenant[]>());
        await picker.DidNotReceive().PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_delegates_to_picker_when_multiple_tenants() {
        DiscoveredTenant[] list = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(list, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(list, Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DiscoveredTenant?>(list[1]));

        var discovery = new TenantDiscovery(proxy, picker);
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked!.OrgLogin).IsEqualTo("contoso");
    }

    [Test]
    public async Task RunAsync_returns_empty_tenant_error_message() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());

        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNull();
        await Assert.That(outcome.ErrorMessage!).Contains("No kcap tenants");
        // The genuine "authenticated but has no tenant" case — this is what should count toward
        // the funnel's cli_setup_tenant_none denominator (see SetupCommand).
        await Assert.That(outcome.NoTenantsFound).IsTrue();
    }

    [Test]
    public async Task RunAsync_returns_proxy_unreachable_error() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.ProxyUnreachable)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("unreachable");
        // A discovery-service failure ALSO returns zero tenants but must NOT be counted as
        // "reached signup, has no tenant" — that was the bug: SetupCommand used to key off
        // outcome.Tenants.Length == 0 alone, which conflated this with the genuine case above.
        await Assert.That(outcome.NoTenantsFound).IsFalse();
    }

    [Test]
    public async Task RunAsync_returns_token_rejected_error() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.TokenRejected)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("GitHub rejected");
        await Assert.That(outcome.NoTenantsFound).IsFalse();
    }

    [Test]
    public async Task RunAsync_returns_upstream_error_message() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.UpstreamError)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("returned an error");
        await Assert.That(outcome.NoTenantsFound).IsFalse();
    }

    [Test]
    public async Task RunAsync_returns_no_tenant_selected_when_picker_returns_null() {
        DiscoveredTenant[] list = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(list, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(list, Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DiscoveredTenant?>(null));

        var discovery = new TenantDiscovery(proxy, picker);
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNull();
        await Assert.That(outcome.ErrorMessage).IsEqualTo("No tenant selected.");
        await Assert.That(outcome.Tenants.Length).IsEqualTo(2);

        // The picker has already said why; the message travels for the caller, unrendered.
        await Assert.That(outcome.AlreadyReported).IsTrue();
    }

    // Every other error here IS discovery's to announce, so the flag must not leak onto them.
    [Test]
    public async Task RunAsync_owns_the_message_for_a_failure_the_picker_had_no_part_in() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.ProxyUnreachable)));

        var outcome = await new TenantDiscovery(proxy, Substitute.For<ITenantPicker>()).RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage).IsNotNull();
        await Assert.That(outcome.AlreadyReported).IsFalse();
    }

    [Test]
    public async Task MergeProfiles_creates_profile_per_tenant_and_sets_active() {
        var existing = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { DefaultVisibility = "org_public" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[0]);

        await Assert.That(merged.ActiveProfile).IsEqualTo("acme");
        await Assert.That(merged.Profiles["acme"].ServerUrl).IsEqualTo("https://a.example");
        await Assert.That(merged.Profiles["contoso"].ServerUrl).IsEqualTo("https://b.example");
        await Assert.That(merged.Profiles["acme"].DefaultVisibility).IsEqualTo("org_public");
    }

    [Test]
    public async Task MergeProfiles_inherits_from_active_profile_when_not_default() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new Dictionary<string, Profile> {
                ["acme"]    = new() { ServerUrl = "https://a.example", DefaultVisibility = "private" },
                ["default"] = new() { DefaultVisibility = "org_public" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",   Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "newly",  Origin = "https://n.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[1]);

        // newly-discovered "newly" profile should inherit settings from active "acme", NOT from "default"
        await Assert.That(merged.Profiles["newly"].DefaultVisibility).IsEqualTo("private");
        await Assert.That(merged.Profiles["newly"].ServerUrl).IsEqualTo("https://n.example");
        await Assert.That(merged.ActiveProfile).IsEqualTo("newly");
    }

    [Test]
    public async Task MergeProfiles_does_not_leak_import_org_into_new_profiles() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new Dictionary<string, Profile> {
                ["acme"] = new() { ServerUrl = "https://a.example", DefaultVisibility = "private", ImportOrg = "AcmeOrg" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",  Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "newly", Origin = "https://n.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[1]);

        // The remembered import org belongs to "acme" — a brand-new tenant profile
        // must not inherit it (it would silently scope a different tenant's import).
        await Assert.That(merged.Profiles["newly"].ImportOrg).IsNull();
        // Other template settings still flow through to the new profile.
        await Assert.That(merged.Profiles["newly"].DefaultVisibility).IsEqualTo("private");
        // The existing profile keeps its own remembered org.
        await Assert.That(merged.Profiles["acme"].ImportOrg).IsEqualTo("AcmeOrg");
    }

    [Test]
    public async Task MergeProfiles_preserves_existing_profile_settings() {
        var existing = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["acme"] = new() { ServerUrl = "https://old", DefaultVisibility = "private" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme", Origin = "https://new.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[0]);

        await Assert.That(merged.Profiles["acme"].ServerUrl).IsEqualTo("https://new.example");
        await Assert.That(merged.Profiles["acme"].DefaultVisibility).IsEqualTo("private");
    }

    [Test]
    public async Task MergeProfiles_uses_slug_for_workos_tenants() {
        var existing = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> { ["default"] = new() { DefaultVisibility = "private" } }
        };
        DiscoveredTenant[] discovered = [
            new() {
                Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous",
                DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai"
            }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[0]);

        await Assert.That(merged.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(merged.Profiles["eventuous"].ServerUrl).IsEqualTo("https://eventuous.kcap.ai");
    }

    /// <summary>
    /// A tenant whose server an existing profile already names takes that profile's name, the
    /// active one first among twins; a tenant on a server no profile names keeps its slug. The
    /// merge then leaves one profile per server.
    /// </summary>
    [Test]
    public async Task NameProfiles_reuses_the_profile_already_on_the_tenants_server() {
        var existing = new ProfileConfig {
            ActiveProfile = "kurrent",
            Profiles = new Dictionary<string, Profile> {
                ["default"]   = new(),
                ["kurrent"]   = new() { ServerUrl = "https://kurrent.kcap.ai", DefaultVisibility = "private" },
                ["eventuous"] = new() { ServerUrl = "https://eventuous.kcap.ai/" },
                ["evt-twin"]  = new() { ServerUrl = "https://eventuous.kcap.ai" },
            }
        };
        DiscoveredTenant[] discovered = [
            new() { Provider = "WorkOS", OrganizationId = "org_k", Slug = "kurrent-io", Origin = "https://kurrent.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_e", Slug = "Eventuous",  Origin = "https://eventuous.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_c", Slug = "contoso",    Origin = "https://contoso.kcap.ai" },
        ];

        var (tenants, picked) = TenantDiscovery.NameProfiles(existing, discovered, discovered[0]);

        await Assert.That(tenants.Select(t => t.ProfileName)).IsEquivalentTo(["kurrent", "eventuous", "contoso"]);
        await Assert.That(picked.ProfileName).IsEqualTo("kurrent");

        var merged = TenantDiscovery.MergeProfiles(existing, tenants, picked);

        await Assert.That(merged.Profiles.Keys).IsEquivalentTo(["default", "kurrent", "eventuous", "evt-twin", "contoso"]);
        await Assert.That(merged.ActiveProfile).IsEqualTo("kurrent");
        await Assert.That(merged.Profiles["kurrent"].DefaultVisibility).IsEqualTo("private");
    }

    /// <summary>
    /// Two tenants never share a profile: a slug that names the profile another tenant reuses for
    /// its own server takes a numbered suffix rather than repointing that profile, and a profile with
    /// no server matches none.
    /// </summary>
    [Test]
    public async Task NameProfiles_never_gives_two_tenants_one_name() {
        var existing = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["acme"]    = new() { ServerUrl = "https://a.example" },
                ["acme-2"]  = new() { ServerUrl = "" },
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://b.example" },
            new() { OrgId = 2, OrgLogin = "acme-io", Origin = "https://a.example" },
        ];

        var (tenants, picked) = TenantDiscovery.NameProfiles(existing, discovered, discovered[0]);

        await Assert.That(tenants[0].ProfileName).IsEqualTo("acme-3");
        await Assert.That(tenants[1].ProfileName).IsEqualTo("acme");
        await Assert.That(picked.ProfileName).IsEqualTo("acme-3");
        await Assert.That(TenantDiscovery.NamesHold(existing, discovered, discovered[0], tenants, picked)).IsTrue();
    }

    /// <summary>Workspaces sharing a server reuse only a profile named for one of them, whatever order
    /// they arrive in; the server's other profiles belong to neither, so each unnamed workspace gets its
    /// own new profile rather than one picked by discovery order.</summary>
    [Test]
    public async Task NameProfiles_reuses_only_named_profiles_on_a_shared_server() {
        var existing = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["work"]    = new() { ServerUrl = "https://a.example" },
                ["other"]   = new() { ServerUrl = "https://a.example" },
            }
        };
        DiscoveredTenant[] discovered = [
            new() { Provider = "WorkOS", OrganizationId = "org_1", Slug = "one", Origin = "https://a.example" },
            new() { Provider = "WorkOS", OrganizationId = "org_2", Slug = "two", Origin = "https://a.example" },
        ];

        var (tenants, _) = TenantDiscovery.NameProfiles(existing, discovered, discovered[0]);

        await Assert.That(string.Join(",", tenants.Select(t => t.ProfileName))).IsEqualTo("one,two");

        var (reversed, _) = TenantDiscovery.NameProfiles(existing, [discovered[1], discovered[0]], discovered[0]);

        await Assert.That(string.Join(",", reversed.Select(t => t.ProfileName))).IsEqualTo("two,one");

        DiscoveredTenant[] named = [
            new() { Provider = "WorkOS", OrganizationId = "org_2", Slug = "other", Origin = "https://a.example" },
            new() { Provider = "WorkOS", OrganizationId = "org_1", Slug = "work",  Origin = "https://a.example" },
        ];

        var (exact, _) = TenantDiscovery.NameProfiles(existing, named, named[0]);

        await Assert.That(string.Join(",", exact.Select(t => t.ProfileName))).IsEqualTo("other,work");

        DiscoveredTenant[] renamed = [
            new() { Provider = "WorkOS", OrganizationId = "org_2", Slug = "new",   Origin = "https://a.example" },
            new() { Provider = "WorkOS", OrganizationId = "org_2", Slug = "other", Origin = "https://a.example" },
        ];

        var (regrouped, _) = TenantDiscovery.NameProfiles(existing, renamed, renamed[0]);

        await Assert.That(string.Join(",", regrouped.Select(t => t.ProfileName))).IsEqualTo("other,other");
    }

    /// <summary>Of two workspaces with one slug on one server, the same one keeps the slug's profile
    /// whichever order discovery lists them in, and the other never lands on it.</summary>
    [Test]
    public async Task NameProfiles_gives_the_slug_profile_to_the_same_workspace_in_any_order() {
        DiscoveredTenant a = new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme", Origin = "https://a.example" };
        DiscoveredTenant b = new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "acme", Origin = "https://a.example" };

        var (fresh, _) = TenantDiscovery.NameProfiles(ProfileConfig.Fresh(), [b, a], b);
        var saved      = TenantDiscovery.MergeProfiles(ProfileConfig.Fresh(), fresh, fresh[0]);

        foreach (DiscoveredTenant[] discovered in new[] { new[] { a, b }, new[] { b, a } }) {
            var (tenants, _) = TenantDiscovery.NameProfiles(saved, discovered, discovered[0]);
            var byOrg = tenants.ToDictionary(t => t.OrganizationId!, t => t.ProfileName);

            await Assert.That(byOrg["org_a"]).IsEqualTo("acme");
            await Assert.That(byOrg["org_b"]).IsNotEqualTo("acme");
        }
    }

    /// <summary>A tenant the discovery service lists twice is still one profile: rows match on the
    /// provider's own id even when their slugs differ, while two organizations, or one organization
    /// on two servers, stay apart.</summary>
    [Test]
    public async Task NameProfiles_gives_a_repeated_tenant_one_name() {
        DiscoveredTenant[] discovered = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme",     Origin = "https://a.example" },
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme-old", Origin = "https://a.example/" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "acme",     Origin = "https://b.example" },
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme",     Origin = "https://staging.example" },
        ];

        var (tenants, picked) = TenantDiscovery.NameProfiles(ProfileConfig.Fresh(), discovered, discovered[1]);

        await Assert.That(tenants[0].ProfileName).IsEqualTo("acme");
        await Assert.That(tenants[1].ProfileName).IsEqualTo("acme");
        await Assert.That(tenants[2].ProfileName).IsEqualTo("acme-3");
        await Assert.That(tenants[3].ProfileName).IsEqualTo("acme-2");
        await Assert.That(picked.ProfileName).IsEqualTo("acme");
        await Assert.That(TenantDiscovery.MergeProfiles(ProfileConfig.Fresh(), tenants, picked).Profiles.Keys)
            .IsEquivalentTo(["default", "acme", "acme-2", "acme-3"]);
    }

    // A rejected token is the provider's rejection. Telling a WorkOS user that GitHub turned them
    // away sends them to the wrong sign-in.
    [Test]
    public async Task Describe_names_the_provider_that_rejected_the_token() {
        await Assert.That(TenantDiscovery.Describe(DiscoveryError.TokenRejected, AuthProvider.WorkOS))
            .Contains("WorkOS");
        await Assert.That(TenantDiscovery.Describe(DiscoveryError.TokenRejected, AuthProvider.GitHubApp))
            .Contains("GitHub");
        // The service being unreachable is neither provider's doing, so it names neither.
        await Assert.That(TenantDiscovery.Describe(DiscoveryError.ProxyUnreachable, AuthProvider.WorkOS))
            .DoesNotContain("GitHub");
    }
}
