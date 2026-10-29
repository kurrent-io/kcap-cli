using System.Globalization;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed record RailPullRequestMenuItem(PullRequestSubjectDto Subject, string? Title, string Lifecycle) {
    public string Label => string.IsNullOrWhiteSpace(Title) ? $"PR #{Subject.Number}" : Title;
    public string Detail => $"{Subject.Owner}/{Subject.RepoName} #{Subject.Number} · {Lifecycle}";
}

public sealed record RailPullRequestSummary(IReadOnlyList<RailPullRequestMenuItem> MenuItems) {
    RailPullRequestMenuItem? SingleItem => MenuItems.Count == 1 ? MenuItems[0] : null;
    public int Count => MenuItems.Count;
    public PullRequestSubjectDto? Subject => SingleItem?.Subject;
    public string Lifecycle => SingleItem?.Lifecycle ?? "Unknown";
    public string Label => SingleItem is { } item
        ? $"PR #{item.Subject.Number.ToString(CultureInfo.InvariantCulture)}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} PRs";

    public PullRequestStatus Status => new(Lifecycle, Lifecycle.ToLowerInvariant());
    public bool HasLifecycle => SingleItem is not null;
    public string Description => SingleItem is { } item
        ? $"Pull request #{item.Subject.Number.ToString(CultureInfo.InvariantCulture)} · {item.Lifecycle}\n{item.Subject.Owner}/{item.Subject.RepoName}"
        : $"{Count.ToString(CultureInfo.InvariantCulture)} pull requests";
}
