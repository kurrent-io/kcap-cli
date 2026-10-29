using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A PR link as a listing row carries it, in the shapes the PR readers take.
public static class ListedPullRequest {
    /// The listing names no provider or host, so they come from the URL as the server derives them
    /// for its PR routes: a subject the panel then opens is the one those routes know.
    public static PullRequestSubjectDto Subject(RepoSessionPullRequestDto pr) {
        var url = PullRequestWire.SafeLink(pr.Url) is { } safe ? new Uri(safe) : null;
        var owner = pr.Owner.ToLowerInvariant();
        var repo = pr.RepoName.ToLowerInvariant();
        var path = $"/{owner}/{repo}/pull/{pr.Number.ToString(CultureInfo.InvariantCulture)}";
        var github = url?.Host == "github.com" && string.Equals(url.AbsolutePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);
        return new() { Provider = github ? "github" : "unknown", Host = url?.IdnHost.ToLowerInvariant() ?? "unknown",
            RepoHash = pr.RepoHash, Owner = owner, RepoName = repo, Number = pr.Number };
    }

    public static PullRequestLinkDto Link(RepoSessionPullRequestDto pr) {
        var subject = Subject(pr);
        return new() { Provider = subject.Provider, Host = subject.Host, RepoHash = subject.RepoHash, Owner = subject.Owner,
            RepoName = subject.RepoName, Number = subject.Number, Url = PullRequestWire.SafeLink(pr.Url), Title = pr.Title, Lifecycle = pr.State };
    }
}
