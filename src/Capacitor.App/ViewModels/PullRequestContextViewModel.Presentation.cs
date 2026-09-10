using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    public bool HasMultipleChoices => _choices.Count > 1;
    public string RepositoryLabel => _selected is { } choice ? $"{choice.Link.Owner}/{choice.Link.RepoName}" : "";
    public string NumberLabel => _selected is { } choice ? "#" + choice.Link.Number.ToString(CultureInfo.InvariantCulture) : "";
    public string ProviderLabel => _selected?.Subject.Provider switch { "github" => "GitHub", "gitlab" => "GitLab", _ => "Source" };
    public bool CanOpenSource => _selected is { IsAvailable: true };
    public bool IsChecks => _section == "checks";
    public bool IsReviewers => _section == "reviewers";
    public bool IsReviewSection => _section is "reviewers" or "reviews" or "threads" or "thread_comments";
    public bool IsDiscussion => _section is "reviews" or "threads" or "thread_comments" or "conversation";
    public IReadOnlyList<PullRequestRow> ReviewerRows => IsReviewers ? Rows : [];
    public IReadOnlyList<PullRequestRow> CheckRows => IsChecks ? Rows : [];
    public IReadOnlyList<PullRequestRow> DiscussionRows => IsDiscussion ? Rows : [];
    public string FreshnessLabel => IsOverview ? FetchedLabel : SnapshotLabel;
    public bool HasStaleOverview => CanDisplay && (_grace || _overviewRead?.Kind == PullRequestReadKind.Stale);
    public string OverviewFreshnessLabel => HasStaleOverview ? "Earlier snapshot" + (FetchedLabel.Length > 0 ? " · " + FetchedLabel : "") : FetchedLabel;

    public int SelectedTabIndex {
        get => _section switch { "checks" => 1, "reviewers" or "reviews" or "threads" or "thread_comments" => 2, "conversation" => 3, _ => 0 };
        set {
            if (value is < 0 or > 3 || value == SelectedTabIndex) return;
            ShowSection(value switch { 0 => "overview", 1 => "checks", 2 => "reviewers", 3 => "conversation", _ => _section });
            this.RaisePropertyChanged();
        }
    }
    public int SelectedReviewTabIndex {
        get => _section switch { "reviews" => 1, "threads" => 2, "thread_comments" => 3, _ => 0 };
        set {
            if (value is < 0 or > 2 || !IsReviewSection || value == SelectedReviewTabIndex) return;
            ShowSection(value switch { 0 => "reviewers", 1 => "reviews", 2 => "threads", _ => _section });
            this.RaisePropertyChanged();
        }
    }

    public PullRequestStatus LifecycleStatus => new(Lifecycle, CanDisplay ? _overview?.Lifecycle ?? "neutral" : "neutral");
    public PullRequestStatus ReviewStatus => new(ReviewSummary, CanDisplay ? _overview?.ReviewDecision switch {
        "approved" => "success", "changes_requested" => "failure", "review_required" => "pending", _ => "neutral"
    } : "neutral");
    public PullRequestStatus ChecksStatus {
        get {
            if (!CanDisplay) return new("");
            var checks = _sections.GetValueOrDefault("checks");
            if (checks is { Coverage: "complete", Stopped: false, Total.Kind: "exact", Completed: { } completed }
                && checks.Head == _overview?.HeadSha && _time.GetUtcNow().UtcDateTime - completed < TimeSpan.FromSeconds(30)
                && completed >= _overview?.Checks?.Availability.FetchedAt && checks.Pages.Sum(page => page.Rows.Length) == checks.Total.Value) {
                var rows = checks.Pages.SelectMany(page => page.Rows).ToArray();
                if (rows.Length == 0) return new("No checks reported", Detail: "No checks reported");
                var failed = rows.Count(row => row.Outcome is "failure" or "timed_out" or "action_required");
                var pending = rows.Count(row => row.Outcome == "pending");
                var passed = rows.Count(row => row.Outcome == "success");
                var other = rows.Length - failed - pending - passed;
                var detail = $"{failed} failed · {pending} pending · {passed} passed" + (other > 0 ? $" · {other} other" : "");
                return failed > 0 ? new($"{failed} failed", "failure", detail)
                    : pending > 0 ? new($"{pending} pending", "pending", detail)
                    : other > 0 ? new("Checks completed", Detail: detail)
                    : new($"{passed} passed", "success", detail);
            }
            return _overview?.Checks?.Availability.Status != "ready" ? new("Checks unavailable", Detail: "Checks unavailable")
                : _overview.Checks.Rollup switch {
                    "success" => new("Checks passing", "success", "GitHub summary: successful"),
                    "failure" => new("Checks failing", "failure", "GitHub summary: failing"),
                    "pending" => new("Checks pending", "pending", "GitHub summary: pending"),
                    _ => new("Checks unknown", Detail: "GitHub summary: unknown")
                };
        }
    }

    void NotifyPresentation() {
        foreach (var property in new[] { nameof(HasMultipleChoices), nameof(RepositoryLabel), nameof(NumberLabel), nameof(ProviderLabel),
            nameof(CanOpenSource), nameof(IsChecks), nameof(IsReviewers), nameof(IsReviewSection), nameof(IsDiscussion), nameof(FreshnessLabel),
            nameof(HasStaleOverview), nameof(OverviewFreshnessLabel),
            nameof(SelectedTabIndex), nameof(SelectedReviewTabIndex), nameof(LifecycleStatus), nameof(ReviewStatus), nameof(ChecksStatus),
            nameof(ReviewerRows), nameof(CheckRows), nameof(DiscussionRows) })
            this.RaisePropertyChanged(property);
    }
}
