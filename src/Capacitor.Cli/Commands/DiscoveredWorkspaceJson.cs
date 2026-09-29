namespace Capacitor.Cli.Commands;

/// <summary>One workspace this account can reach.</summary>
/// <param name="Slug">The workspace's own name in its hostname. Null on a GitHub-App row, which
/// identifies a workspace by origin rather than slug.</param>
/// <param name="Url">What to hand back as <c>--server-url</c>.</param>
/// <param name="Name">Display name, when the provider gives one.</param>
public sealed record DiscoveredWorkspaceJson(string? Slug, string Url, string? Name);
