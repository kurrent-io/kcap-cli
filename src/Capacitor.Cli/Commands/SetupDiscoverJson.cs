namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap setup --discover --json</c>.</summary>
/// <param name="Workspaces">Every workspace the sign-in could see, which may be none.</param>
/// <param name="CanCreate">No workspace was found, on the one lane that can create one — never true
/// for a GitHub-App account, which gets a workspace by having the app installed on an org. Not a
/// promise that creating will succeed: a workspace already being made for this account is only
/// learned by the create call itself.</param>
/// <param name="Provider">The sign-in route discovery took. One value for the whole result: every
/// row came back from that lane.</param>
public sealed record SetupDiscoverJson(
    IReadOnlyList<DiscoveredWorkspaceJson> Workspaces, bool CanCreate, string Provider);
