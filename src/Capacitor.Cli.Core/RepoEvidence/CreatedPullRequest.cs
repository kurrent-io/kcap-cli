namespace Capacitor.Cli.Core.RepoEvidence;

public sealed record CreatedPullRequest(string Owner, string RepoName, int Number, string Url);
