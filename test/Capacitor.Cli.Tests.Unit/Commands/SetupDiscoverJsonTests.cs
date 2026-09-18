using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupDiscoverJsonTests {
    static DiscoveredTenant Tenant(string slug, string origin, string? display = null, string? provider = null) =>
        new() {
            Slug        = slug,
            Origin      = origin,
            DisplayName = display,
            Provider    = provider ?? AuthProvider.WorkOS,
        };

    [Test]
    public async Task Renders_a_workspace_row_snake_cased() {
        var payload = SetupDiscoverRender.Payload(
            [Tenant("acme", "https://acme.kcap.ai", "Acme Corp")], AuthProvider.WorkOS);

        using var doc = JsonDocument.Parse(SetupDiscoverRender.Render(payload));
        var row = doc.RootElement.GetProperty("workspaces")[0];

        await Assert.That(row.GetProperty("slug").GetString()).IsEqualTo("acme");
        await Assert.That(row.GetProperty("url").GetString()).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("Acme Corp");
        await Assert.That(doc.RootElement.GetProperty("provider").GetString()).IsEqualTo(AuthProvider.WorkOS);
    }

    // Only an account with no workspace can create one, so the flag is derived rather than guessed.
    [Test]
    public async Task Can_create_only_when_the_account_has_no_workspace() {
        await Assert.That(SetupDiscoverRender.Payload([], AuthProvider.WorkOS).CanCreate).IsTrue();
        await Assert.That(SetupDiscoverRender.Payload([Tenant("acme", "https://acme.kcap.ai")], AuthProvider.WorkOS).CanCreate)
            .IsFalse();
    }

    // A GitHub-App row identifies a workspace by origin and carries no slug, so the field is
    // nullable rather than backfilled with something that is not the workspace's name.
    [Test]
    public async Task A_row_without_a_slug_or_display_name_reports_nulls_not_blanks() {
        var payload = SetupDiscoverRender.Payload(
            [new DiscoveredTenant { Origin = "https://acme.kcap.ai", DisplayName = "  " }], AuthProvider.GitHubApp);

        await Assert.That(payload.Workspaces[0].Slug).IsNull();
        await Assert.That(payload.Workspaces[0].Name).IsNull();
        await Assert.That(payload.Workspaces[0].Url).IsEqualTo("https://acme.kcap.ai");
    }

    // The whole point of the reporting picker: it sees the rows and declines, and a decline is
    // strictly pre-boundary, so discovery publishes nothing.
    [Test]
    public async Task The_reporting_picker_records_the_rows_and_declines() {
        var picker  = new ReportingTenantPicker();
        var tenants = new[] { Tenant("acme", "https://acme.kcap.ai"), Tenant("beta", "https://beta.kcap.ai") };

        var picked = await picker.PickAsync(tenants, TenantPickContext.None, CancellationToken.None);

        await Assert.That(picked).IsNull();
        await Assert.That(picker.Offered.Select(t => t.Slug!)).IsEquivalentTo(["acme", "beta"]);
    }

    [Test]
    public async Task The_reporting_picker_declines_on_the_synchronous_seam_too() {
        var picker = new ReportingTenantPicker();

        await Assert.That(picker.Pick([Tenant("acme", "https://acme.kcap.ai")])).IsNull();
        await Assert.That(picker.Offered.Count).IsEqualTo(1);
    }
}
