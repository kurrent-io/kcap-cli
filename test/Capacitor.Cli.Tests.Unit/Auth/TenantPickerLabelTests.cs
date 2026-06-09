using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Auth;

public class TenantPickerLabelTests {
    [Test]
    public async Task Formats_user_tenant_as_at_username_with_personal_suffix() {
        var label = TenantPickerLabel.Render(new DiscoveredTenant {
            AccountType  = "User",
            AccountLogin = "alice",
            OrgLogin     = "alice",
            Origin       = "https://kapacitor.alice.example"
        });
        await Assert.That(label).IsEqualTo("@alice (personal)");
    }

    [Test]
    public async Task Formats_org_tenant_as_org_login() {
        var label = TenantPickerLabel.Render(new DiscoveredTenant {
            AccountType  = "Organization",
            AccountLogin = "acme",
            OrgLogin     = "acme",
            Origin       = "https://kapacitor.acme.example"
        });
        await Assert.That(label).IsEqualTo("acme");
    }

    [Test]
    public async Task Falls_back_to_OrgLogin_when_AccountLogin_empty() {
        // Defensive — older proxy responses might only set OrgLogin.
        var label = TenantPickerLabel.Render(new DiscoveredTenant {
            AccountType  = "Organization",
            AccountLogin = "",
            OrgLogin     = "acme",
            Origin       = "https://kapacitor.acme.example"
        });
        await Assert.That(label).IsEqualTo("acme");
    }

    [Test]
    public async Task User_tenant_falls_back_to_OrgLogin_when_AccountLogin_empty() {
        // Defensive — older proxy responses might only set OrgLogin even for User accounts.
        var label = TenantPickerLabel.Render(new DiscoveredTenant {
            AccountType  = "User",
            AccountLogin = "",
            OrgLogin     = "bob",
            Origin       = "https://kapacitor.bob.example"
        });
        await Assert.That(label).IsEqualTo("@bob (personal)");
    }

    [Test]
    public async Task Unknown_account_type_renders_as_plain_login() {
        var label = TenantPickerLabel.Render(new DiscoveredTenant {
            AccountType  = "",
            AccountLogin = "mystery",
            OrgLogin     = "mystery",
            Origin       = "https://kapacitor.mystery.example"
        });
        await Assert.That(label).IsEqualTo("mystery");
    }
}
