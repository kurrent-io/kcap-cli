using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Onboarding;

/// Taken from Core's registry so the app and the CLI enumerate the same vendors, in the same order —
/// a new harness appears here without an edit.
internal static class AgentVendors {
    public static readonly IReadOnlyList<AgentVendor> All =
        [.. HarnessRegistry.Identities.Select(h => new AgentVendor(h.Id, h.Label, h.Id.PluginInstallFlag))];
}
