using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    public bool HasMultipleChoices => _choices.Count > 1;
    /// Always plural: identity lives on the card; the eyebrow only names how many are linked.
    public string SectionEyebrow => "PULL REQUESTS";
    public string SectionMeta => _choices.Count.ToString(CultureInfo.InvariantCulture);
    public string RepositoryLabel => _selected is { } choice ? $"{choice.Link.Owner}/{choice.Link.RepoName}" : "";
    public string NumberLabel => _selected is { } choice ? "#" + choice.Link.Number.ToString(CultureInfo.InvariantCulture) : "";
    public bool HasNumberLabel => NumberLabel.Length > 0;
    public string ProviderLabel => _selected?.Subject.Provider switch { "github" => "GitHub", "gitlab" => "GitLab", _ => "Source" };
    public bool CanOpenSource => _selected is { IsAvailable: true };
    /// Host exit beside status/repo once the card has settled on an overview or a legacy list.
    public bool ShowsOpenSource => CanOpenSource && (CanDisplay || IsLegacy);
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

    public PullRequestStatus LifecycleStatus => CanDisplay ? PullRequestTones.LifecycleStatus(_overview) : new("");
    public PullRequestStatus ReviewStatus => CanDisplay
        ? _overview?.ReviewDecision switch {
            "approved" => new("Approved", "success", "All required reviews are approved."),
            "changes_requested" => new("Changes requested", "failure", "A reviewer requested changes before merge."),
            "review_required" => new("Review required", "warning", "Waiting on a required review before this PR can merge."),
            _ => new("Review decision unknown", "neutral", "GitHub has not reported a review decision yet."),
        }
        : new("");
    public PullRequestStatus ChecksStatus {
        get {
            if (!CanDisplay) return new("");
            var checks = _sections.GetValueOrDefault("checks");
            if (checks is { Coverage: "complete", Stopped: false, Total.Kind: "exact", Completed: { } completed }
                && checks.Head == _overview?.HeadSha && _time.GetUtcNow().UtcDateTime - completed < RowsFreshFor
                && completed >= _overview?.Checks?.Availability.FetchedAt && checks.Pages.Sum(page => page.Rows.Length) == checks.Total.Value) {
                var rows = checks.Pages.SelectMany(page => page.Rows).ToArray();
                if (rows.Length == 0) return new("No checks reported", Detail: "No checks were reported for this commit.");
                var failed = rows.Count(row => row.Outcome is "failure" or "timed_out" or "action_required");
                var pending = rows.Count(row => row.Outcome == "pending");
                var passed = rows.Count(row => row.Outcome == "success");
                var other = rows.Length - failed - pending - passed;
                var counts = $"{failed} failed · {pending} pending · {passed} passed" + (other > 0 ? $" · {other} other" : "");
                // Sidebar is a verdict; Tip carries a sentence (and counts when useful). Fail and
                // pending keep a count in Text because that changes urgency; all-green stays short.
                return failed > 0 ? new($"{failed} failed", "failure", $"One or more checks failed ({counts}).")
                    : pending > 0 ? new($"{pending} pending", "pending", $"Checks are still running ({counts}).")
                    : other > 0 ? new("Checks completed", Detail: $"Checks finished with mixed outcomes ({counts}).")
                    : new("Checks passing", "success", $"All checks have passed ({counts}).");
            }
            return _overview?.Checks?.Availability.Status != "ready" ? new("Checks unavailable", Detail: "Checks could not be loaded for this pull request.")
                : _overview.Checks.Rollup switch {
                    "success" => new("Checks passing", "success", "All checks have passed."),
                    "failure" => new("Checks failing", "failure", "One or more checks failed."),
                    "pending" => new("Checks pending", "pending", "Checks are still running."),
                    _ => new("Checks unknown", Detail: "GitHub has not reported a check summary yet.")
                };
        }
    }

    static readonly string[] PresentationProperties = [
        nameof(HasMultipleChoices), nameof(SectionEyebrow), nameof(SectionMeta), nameof(RepositoryLabel), nameof(NumberLabel), nameof(HasNumberLabel), nameof(ProviderLabel),
        nameof(CanOpenSource), nameof(ShowsOpenSource), nameof(IsChecks), nameof(IsReviewers), nameof(IsReviewSection), nameof(IsDiscussion), nameof(FreshnessLabel),
        nameof(HasStaleOverview), nameof(OverviewFreshnessLabel),
        nameof(SelectedTabIndex), nameof(SelectedReviewTabIndex), nameof(LifecycleStatus), nameof(ReviewStatus), nameof(ChecksStatus),
        nameof(ReviewerRows), nameof(CheckRows), nameof(DiscussionRows),
    ];

    void NotifyPresentation() {
        foreach (var property in PresentationProperties) this.RaisePropertyChanged(property);
    }
}
