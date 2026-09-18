using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>One workspace this account can reach.</summary>
/// <param name="Slug">The workspace's own name in its hostname. Null on a GitHub-App row, which
/// identifies a workspace by origin rather than slug.</param>
/// <param name="Url">What to hand back as <c>--server-url</c>.</param>
/// <param name="Name">Display name, when the provider gives one.</param>
public sealed record DiscoveredWorkspaceJson(string? Slug, string Url, string? Name, string Provider);

/// <summary>Machine-readable payload for <c>kcap setup --discover --json</c>.</summary>
/// <param name="Workspaces">Every workspace the sign-in could see, which may be none.</param>
/// <param name="CanCreate">Whether this account may create a workspace — only an account with none
/// can, so this is false the moment <paramref name="Workspaces"/> is non-empty.</param>
/// <param name="Provider">The sign-in route discovery took.</param>
public sealed record SetupDiscoverJson(
    IReadOnlyList<DiscoveredWorkspaceJson> Workspaces, bool CanCreate, string Provider);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SetupDiscoverJson))]
public partial class SetupDiscoverJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the discovery payload — kept separate from I/O so it's directly testable.</summary>
internal static class SetupDiscoverRender {
    public static SetupDiscoverJson Payload(IReadOnlyList<DiscoveredTenant> tenants, string provider) =>
        new(
            [.. tenants.Select(t => new DiscoveredWorkspaceJson(
                t.Slug,
                t.Origin,
                string.IsNullOrWhiteSpace(t.DisplayName) ? null : t.DisplayName,
                t.Provider))],
            CanCreate: tenants.Count == 0,
            provider);

    public static string Render(SetupDiscoverJson payload) =>
        JsonSerializer.Serialize(payload, SetupDiscoverJsonContext.Default.SetupDiscoverJson);
}

/// <summary>
/// The picker for a discovery that must not choose. It records the rows it was offered and answers
/// null, which the façade treats as a cancel — and a cancel is strictly pre-boundary, so nothing is
/// published: no profile written, no workspace activated, no token stored.
///
/// <para>Null also means "the picker has already told the user why", so this one stays silent and
/// the caller renders the report instead.</para>
/// </summary>
internal sealed class ReportingTenantPicker : ITenantPicker {
    public IReadOnlyList<DiscoveredTenant> Offered { get; private set; } = [];

    public DiscoveredTenant? Pick(DiscoveredTenant[] tenants) {
        Offered = tenants;

        return null;
    }

    public Task<DiscoveredTenant?> PickAsync(
            DiscoveredTenant[] tenants, TenantPickContext context, CancellationToken ct) {
        Offered = tenants;

        return Task.FromResult<DiscoveredTenant?>(null);
    }
}
