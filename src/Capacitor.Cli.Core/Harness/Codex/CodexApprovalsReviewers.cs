namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex's <c>approvals_reviewer</c> tokens. <see cref="AutoReview"/> hands each approval request to
/// Codex's own reviewer subagent, whose denial is final; <see cref="User"/> prompts the person.
public static class CodexApprovalsReviewers {
    public const string User       = "user";
    public const string AutoReview = "auto_review";
}
