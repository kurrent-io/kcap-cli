using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>One workspace this account can reach.</summary>
/// <param name="Slug">The workspace's own name in its hostname. Null on a GitHub-App row, which
/// identifies a workspace by origin rather than slug.</param>
/// <param name="Url">What to hand back as <c>--server-url</c>.</param>
/// <param name="Name">Display name, when the provider gives one.</param>
public sealed record DiscoveredWorkspaceJson(string? Slug, string Url, string? Name);

/// <summary>Machine-readable payload for <c>kcap setup --discover --json</c>.</summary>
/// <param name="Workspaces">Every workspace the sign-in could see, which may be none.</param>
/// <param name="CanCreate">Whether this account may create a workspace. Only the hosted lane
/// provisions, and only for an account that belongs to none — a GitHub-App account gets a workspace
/// by having the app installed on an org, so it is never true there.</param>
/// <param name="Provider">The sign-in route discovery took. One value for the whole result: every
/// row came back from that lane.</param>
public sealed record SetupDiscoverJson(
    IReadOnlyList<DiscoveredWorkspaceJson> Workspaces, bool CanCreate, string Provider);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SetupDiscoverJson))]
public partial class SetupDiscoverJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the discovery payload — kept separate from I/O so it's directly testable.</summary>
internal static class SetupDiscoverRender {
    public static SetupDiscoverJson Payload(DiscoveryReport report) =>
        new(
            [.. report.Tenants.Select(t => new DiscoveredWorkspaceJson(
                string.IsNullOrWhiteSpace(t.Slug) ? null : t.Slug,
                t.Origin,
                string.IsNullOrWhiteSpace(t.DisplayName) ? null : t.DisplayName))],
            report.CanCreate,
            report.Provider);

    public static string Render(SetupDiscoverJson payload) =>
        JsonSerializer.Serialize(payload, SetupDiscoverJsonContext.Default.SetupDiscoverJson);
}
