using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The answers the create-a-workspace prompts collect, supplied up front by <c>--org</c>,
/// <c>--slug</c>, <c>--tier</c> and <c>--invite-code</c> instead. Deliberately not on <c>ITenantProvisioner</c>: only the CLI has flags to
/// fill it from, and the other host driving that interface prompts through its own UI.
/// </summary>
public sealed record RequestedWorkspace(
        string OrgName, string Slug, WorkspaceTier Tier = WorkspaceTier.Free, string? InviteCode = null) {
    /// <summary>Expanded the same way `kcap setup &lt;tenant&gt;` expands a bare label.</summary>
    public string Origin => ServerInput.ResolveTenantArg(Slug);
}
