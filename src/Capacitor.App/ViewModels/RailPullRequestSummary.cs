using System.Globalization;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record RailPullRequestMenuItem(PullRequestSubjectDto Subject, string? Title, string Lifecycle) {
    public string Label => string.IsNullOrWhiteSpace(Title) ? $"PR #{Subject.Number}" : Title;
    public string Detail => $"{Subject.Owner}/{Subject.RepoName} #{Subject.Number} · {Lifecycle}";
}

public sealed record RailPullRequestSummary(int Count, PullRequestSubjectDto? Subject = null, string Lifecycle = "Unknown",
        IReadOnlyList<RailPullRequestMenuItem>? Items = null) {
    public IReadOnlyList<RailPullRequestMenuItem> MenuItems => Items ?? [];
    public string Label => Count == 1
        ? $"PR #{Subject!.Number.ToString(CultureInfo.InvariantCulture)}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} PRs";

    public PullRequestStatus Status => new(Lifecycle, Lifecycle.ToLowerInvariant());
    public bool HasLifecycle => Count == 1;
    public string Description => Count == 1
        ? $"Pull request #{Subject!.Number.ToString(CultureInfo.InvariantCulture)} · {Lifecycle}\n{Subject.Owner}/{Subject.RepoName}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} pull requests";
}
