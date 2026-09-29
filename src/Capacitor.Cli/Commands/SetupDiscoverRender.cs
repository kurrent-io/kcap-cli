using System.Text.Json;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

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
