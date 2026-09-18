using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// <param name="IsListed">False for a PR the work item links but the session's list did not: a
/// local reader can still serve it, the server reader refuses it.</param>
public sealed record PullRequestChoice(PullRequestLinkDto Link, bool IsAvailable = true, bool IsListed = true) {
    public PullRequestSubjectDto Subject { get; } = PullRequestWire.Subject(Link);
    public string Label => $"{Link.Owner}/{Link.RepoName} #{Link.Number}" + (IsAvailable ? "" : " · Unavailable");
    public override string ToString() => Label;
}
