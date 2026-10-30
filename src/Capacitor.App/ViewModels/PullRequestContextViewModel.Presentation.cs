using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    public bool HasMultipleChoices => _choices.Count > 1;
    /// Always plural: identity lives on the card; the eyebrow only names how many are linked.
    public string SectionEyebrow => "PULL REQUESTS";
    public string SectionMeta => _choices.Count > 0 ? _choices.Count.ToString(CultureInfo.InvariantCulture) : "";
    public string RepositoryLabel => _selected is { } choice ? $"{choice.Link.Owner}/{choice.Link.RepoName}" : "";
    public string NumberLabel => _selected is { } choice ? "#" + choice.Link.Number.ToString(CultureInfo.InvariantCulture) : "";
    public bool HasNumberLabel => NumberLabel.Length > 0;
    /// The repository GitHub references in a body are read against; none off github.com, where the
    /// reader cannot tell a reference from text.
    public string? ReferenceBase => _selected?.Subject is { } subject && PullRequestWire.IsGitHub(subject)
        ? $"https://github.com/{Uri.EscapeDataString(subject.Owner)}/{Uri.EscapeDataString(subject.RepoName)}"
        : null;

    public string ProviderLabel => _selected?.Subject.Provider switch { "github" => "GitHub", "gitlab" => "GitLab", _ => "Source" };
    public bool CanOpenSource => _selected is { IsAvailable: true };
    /// Host exit beside status/repo once the card has settled on an overview or a legacy list.
    public bool ShowsOpenSource => CanOpenSource && (CanDisplay || IsLegacy);
    /// Overview and conflicts render from the overview read alone; every other section pages rows.
    bool ReadsRows => _section is not ("overview" or "conflicts");
    public bool IsConflicts => _section == "conflicts";
    public bool HasConflicts => CanDisplay && _overview is { Lifecycle: "open" or "draft", Mergeable: false };
    /// Stays while selected, so the tab under the reader does not vanish when a poll clears the conflict.
    public bool ShowsConflictsTab => HasConflicts || IsConflicts;
    public PullRequestStatus ConflictsStatus => HasConflicts ? new("Merge conflicts", "conflict") : new("");
    /// "No conflicts" only on a displayed overview that says so; an unknown or hidden answer is not a clean one.
    public string ConflictsNote =>
        !CanDisplay ? "Refresh access to open PR content."
        : HasConflicts ? $"This branch has conflicts with {_overview?.BaseRef ?? "the base branch"} that must be resolved before it can merge."
        : _overview is not { Lifecycle: "open" or "draft" } ? "This pull request is no longer open."
        : _overview.Mergeable == true ? "This branch has no conflicts with the base branch."
        : "GitHub has not reported whether this branch merges cleanly.";
    public bool IsChecks => _section == "checks";
    public bool IsReviewers => _section == "reviewers";
    public bool IsReviewSection => _section is "reviewers" or "reviews" or "threads" or "thread_comments";
    public bool IsDiscussion => _section is "reviews" or "threads" or "thread_comments" or "conversation";
    public IReadOnlyList<PullRequestRow> ReviewerRows => IsReviewers ? _reviewerRows : [];
    public IReadOnlyList<PullRequestRow> CheckRows => IsChecks ? _checkRows : [];
    public IReadOnlyList<PullRequestRow> DiscussionRows => IsDiscussion ? _discussionRows : [];
    public string FreshnessLabel {
        get {
            var time = !ReadsRows || HasStaleOverview ? FetchedLabel : SnapshotLabel;
            return HasStaleOverview ? "Earlier snapshot" + (time.Length > 0 ? " · " + time : "") : time;
        }
    }
    public bool HasStaleOverview => CanDisplay && (_grace || _overviewRead?.Kind == PullRequestReadKind.Stale);
    public string OverviewFreshnessLabel => HasStaleOverview ? "Earlier snapshot" + (FetchedLabel.Length > 0 ? " · " + FetchedLabel : "") : FetchedLabel;

    public int SelectedTabIndex {
        get => _section switch { "checks" => 1, "reviewers" or "reviews" or "threads" or "thread_comments" => 2, "conversation" => 3, "conflicts" => 4, _ => 0 };
        set {
            if (value is < 0 or > 4 || value == SelectedTabIndex) return;
            ShowSection(value switch { 0 => "overview", 1 => "checks", 2 => "reviewers", 3 => "conversation", 4 => "conflicts", _ => _section });
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
                && checks.Head == _overview?.HeadSha && (HasStaleOverview || _time.GetUtcNow().UtcDateTime - completed < RowsFreshFor)
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
                    : pending > 0 ? new($"Checks pending · {rows.Length - pending}/{rows.Length}", "pending", $"Checks are still running ({counts}).")
                    : other > 0 ? new("Checks completed", Detail: $"Checks finished with mixed outcomes ({counts}).")
                    : new("Checks passing", "success", $"All checks have passed ({counts}).");
            }
            return _overview?.Checks?.Availability.Status != "ready" ? new("Checks unavailable", Detail: "Checks could not be loaded for this pull request.")
                : _overview.Checks.Rollup switch {
                    "success" => new("Checks passing", "success", "All checks have passed."),
                    "failure" => new("Checks failing", "failure", "One or more checks failed."),
                    "pending" => new("Checks pending" + Progress(_overview.Checks.Counts), "pending", "Checks are still running."),
                    _ => new("Checks unknown", Detail: "GitHub has not reported a check summary yet.")
                };
        }
    }

    /// Empty when the overview carries no thread summary, as a server without one sends, or when a zero is only
    /// a lower bound, which says nothing about the threads it could not read.
    public PullRequestStatus ThreadsStatus =>
        CanDisplay && _overview?.Threads is { Availability.Status: "ready", Unresolved: { Value: { } count } unresolved }
            ? count == 0
                ? unresolved.Kind == "exact" ? new("No unresolved threads", "success", "Every review thread is resolved.") : new("")
                : new($"{count}{(unresolved.Kind == "exact" ? "" : "+")} unresolved thread{(count == 1 && unresolved.Kind == "exact" ? "" : "s")}",
                      "warning", "Review threads are still waiting on a reply or a fix.")
            : new("");
    public bool HasThreadsStatus => ThreadsStatus.Text.Length > 0;

    /// What the agent is asked to do about everything that keeps an open pull request from merging, or
    /// null when nothing does.
    internal string? FixPrompt {
        get {
            if (!CanDisplay || _overview is not { Lifecycle: "open" or "draft" } overview) return null;
            var asks = new List<string>();
            if (overview.Mergeable == false) asks.Add("resolve the merge conflicts with the base branch");
            if (overview.Threads is { Availability.Status: "ready", Unresolved: { Value: > 0 and var count } unresolved })
                asks.Add($"address the {count}{(unresolved.Kind == "exact" ? "" : "+")} unresolved review thread{(count == 1 && unresolved.Kind == "exact" ? "" : "s")}");
            if (ChecksStatus.Kind == "failure") asks.Add("fix the failing checks");
            return asks.Count == 0 ? null : $"Get pull request {NumberLabel} ready to merge: {string.Join("; ", asks)}.";
        }
    }
    /// The draft the user has not sent stays, with the prompt after it; a draft already holding the prompt is left as is.
    internal static string WithFixPrompt(string draft, string prompt) =>
        string.IsNullOrWhiteSpace(draft) ? prompt
        : draft.Contains(prompt, StringComparison.Ordinal) ? draft
        : draft.TrimEnd() + "\n\n" + prompt;

    public bool ShowsFix => FixPrompt is not null && _canRequestFix();

    /// Call when the host's composer gains or loses the ability to send, so <see cref="ShowsFix"/> is read again.
    internal void FixAvailabilityChanged() => NotifyValue(ShowsFix);

    // A capped list reports lower bounds, and a fraction built from them would misstate the total.
    static string Progress(Dictionary<string, PullRequestCountDto>? counts) {
        if (counts is not { Count: > 0 } || counts.Values.Any(count => count is not { Kind: "exact", Value: not null })) return "";
        var total = counts.Values.Sum(count => count.Value!.Value);
        var done = total - (counts.GetValueOrDefault("pending")?.Value ?? 0);
        return $" · {done}/{total}";
    }

    void NotifyPresentation() {
        NotifyValue(HasMultipleChoices);
        NotifyValue(SectionEyebrow);
        NotifyValue(SectionMeta);
        NotifyValue(RepositoryLabel);
        NotifyValue(NumberLabel);
        NotifyValue(HasNumberLabel);
        NotifyValue(ProviderLabel);
        NotifyValue(ReferenceBase);
        NotifyValue(CanOpenSource);
        NotifyValue(ShowsOpenSource);
        NotifyValue(IsConflicts);
        NotifyValue(HasConflicts);
        NotifyValue(ShowsConflictsTab);
        NotifyValue(ConflictsStatus);
        NotifyValue(ConflictsNote);
        NotifyValue(IsChecks);
        NotifyValue(IsReviewers);
        NotifyValue(IsReviewSection);
        NotifyValue(IsDiscussion);
        NotifyValue(FreshnessLabel);
        NotifyValue(HasStaleOverview);
        NotifyValue(OverviewFreshnessLabel);
        NotifyValue(SelectedTabIndex);
        NotifyValue(SelectedReviewTabIndex);
        NotifyValue(LifecycleStatus);
        NotifyValue(ReviewStatus);
        NotifyValue(ChecksStatus);
        NotifyValue(ThreadsStatus);
        NotifyValue(HasThreadsStatus);
        NotifyValue(ShowsFix);
        NotifyValue(!ReferenceEquals(ReviewerRows, _reviewerRows), nameof(ReviewerRows));
        NotifyValue(!ReferenceEquals(CheckRows, _checkRows), nameof(CheckRows));
        NotifyValue(!ReferenceEquals(DiscussionRows, _discussionRows), nameof(DiscussionRows));
    }
}
