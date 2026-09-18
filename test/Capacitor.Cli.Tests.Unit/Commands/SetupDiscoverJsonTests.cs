using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupDiscoverJsonTests {
    static DiscoveredTenant Tenant(string? slug, string origin, string? display = null) =>
        new() { Slug = slug, Origin = origin, DisplayName = display };

    static DiscoveryReport Report(DiscoveredTenant[] tenants, string provider, bool canCreate) =>
        new(tenants, provider, canCreate);

    [Test]
    public async Task Renders_a_workspace_row_snake_cased() {
        var payload = SetupDiscoverRender.Payload(
            Report([Tenant("acme", "https://acme.kcap.ai", "Acme Corp")], AuthProvider.WorkOS, canCreate: false));

        using var doc = JsonDocument.Parse(SetupDiscoverRender.Render(payload));
        var row = doc.RootElement.GetProperty("workspaces")[0];

        await Assert.That(row.GetProperty("slug").GetString()).IsEqualTo("acme");
        await Assert.That(row.GetProperty("url").GetString()).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("Acme Corp");
        await Assert.That(doc.RootElement.GetProperty("provider").GetString()).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(doc.RootElement.GetProperty("can_create").GetBoolean()).IsFalse();
    }

    // A GitHub-App row identifies a workspace by origin and carries no slug, so the field is
    // nullable rather than backfilled with something that is not the workspace's name.
    [Test]
    public async Task A_row_without_a_slug_or_display_name_reports_nulls_not_blanks() {
        var payload = SetupDiscoverRender.Payload(
            Report([Tenant(null, "https://acme.kcap.ai", "  ")], AuthProvider.GitHubApp, canCreate: false));

        await Assert.That(payload.Workspaces[0].Slug).IsNull();
        await Assert.That(payload.Workspaces[0].Name).IsNull();
        await Assert.That(payload.Workspaces[0].Url).IsEqualTo("https://acme.kcap.ai");
    }

    // can_create is the lane's answer, not a count: only the hosted lane provisions, so a GitHub
    // account with no workspace must not be told it can make one.
    [Test]
    public async Task Can_create_comes_from_the_report_rather_than_an_empty_list() {
        await Assert.That(SetupDiscoverRender.Payload(Report([], AuthProvider.GitHubApp, canCreate: false)).CanCreate)
            .IsFalse();
        await Assert.That(SetupDiscoverRender.Payload(Report([], AuthProvider.WorkOS, canCreate: true)).CanCreate)
            .IsTrue();
    }

    [Test]
    public async Task An_account_with_no_workspace_reports_an_empty_list_rather_than_an_error() {
        var payload = SetupDiscoverRender.Payload(Report([], AuthProvider.WorkOS, canCreate: true));

        await Assert.That(payload.Workspaces.Count).IsEqualTo(0);

        using var doc = JsonDocument.Parse(SetupDiscoverRender.Render(payload));

        await Assert.That(doc.RootElement.GetProperty("workspaces").GetArrayLength()).IsEqualTo(0);
        await Assert.That(doc.RootElement.GetProperty("can_create").GetBoolean()).IsTrue();
    }

    // A failure carries no rows and never claims the account can create one.
    [Test]
    public async Task A_failure_report_is_empty_and_cannot_create() {
        var failure = DiscoveryReport.Failure(AuthProvider.WorkOS, "The Kurrent auth service is unreachable.");

        await Assert.That(failure.Tenants.Length).IsEqualTo(0);
        await Assert.That(failure.CanCreate).IsFalse();
        await Assert.That(failure.Error).IsNotNull();
    }
}
