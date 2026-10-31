namespace Capacitor.Cli.Core.Handoff;

/// <summary>The ended session as the server read it. The pull request is the one the start names, when the
/// session links it in its own repository.</summary>
public sealed record HandoffSourceContext(
    string SessionId,
    string? Title,
    string? OwnerDisplay,
    string? RepoOwner,
    string? RepoName,
    string? Branch,
    int? PullRequestNumber,
    string? PullRequestTitle,
    string? FirstPrompt,
    string? LastPrompt
);
