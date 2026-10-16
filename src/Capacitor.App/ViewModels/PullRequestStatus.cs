using Lucide.Avalonia;
namespace Capacitor.App.ViewModels;

public sealed record PullRequestStatus(string Text, string Kind = "neutral", string? Detail = null) {
    /// Settled outcomes only — a merge, or a passing check/review. Open is live work, not success.
    public bool IsSuccess => Kind is "success" or "merged";
    public bool IsWarning => Kind is "conflict" or "warning";
    public bool IsDanger => Kind is "failure" or "closed";
    public bool IsPulsing => Kind == "pending";
    /// Git lifecycle marks and outcome circles share Lucide strokes. In-flight / waiting
    /// circles keep their activity dots and pulses, like running subagents.
    public bool UsesGlyphIcon => Kind is "open" or "draft" or "merged" or "conflict" or "commented";
    public bool UsesDiscIcon => !UsesGlyphIcon;
    public bool IsNeutralDisc => UsesDiscIcon && !IsSuccess && !IsWarning && !IsDanger && !IsPulsing;
    /// Hover copy so colour is never the only cue — Detail when the caller has more, else Kind.
    public string Tip => !string.IsNullOrEmpty(Detail) ? Detail! : Kind switch {
        "open" => "This pull request is open.",
        "draft" => "This pull request is still a draft.",
        "merged" => "This pull request has been merged.",
        "closed" => "This pull request was closed without merging.",
        "conflict" => "This branch has merge conflicts with the base.",
        "success" => Text.Length > 0 ? Text : "Passed.",
        "failure" => Text.Length > 0 ? Text : "Failed.",
        "pending" => "Checks are still running.",
        "warning" => "Waiting on a required review before this PR can merge.",
        "commented" => "Reviewers left comments without approving or requesting changes.",
        _ => Text.Length > 0 ? Text : "Status unknown.",
    };
    public LucideIconKind? IconKind => Kind switch {
        "open" => LucideIconKind.GitPullRequest,
        "draft" => LucideIconKind.GitPullRequestDraft,
        "conflict" => LucideIconKind.TriangleAlert,
        "merged" => LucideIconKind.GitMerge,
        "commented" => LucideIconKind.MessageSquare,
        _ => null,
    };
}
