using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels;

/// A refused hand-off launch, worded. Server and daemon reasons lead with a snake_case code that is
/// never shown: known codes become sentences, and an unknown one keeps only its detail.
public static partial class HandoffFailureText {
    public const string StartChanged =
        "The branch changed after it was checked, so nothing was started. Check the new starting point and start again.";
    public const string SourceUnavailable =
        "You can no longer hand off this session. It may have been deleted, or your access to it changed.";
    public const string InvalidHandoff =
        "Your local kcap daemon couldn't start this hand-off, so nothing was started. Update kcap and try again.";
    public const string RepoMismatch =
        "The checkout on this machine isn't a clone of this session's repository, so nothing was started.";
    public const string RepoNotAllowed =
        "Your local kcap daemon isn't allowed to work in that checkout, so nothing was started.";
    public const string NotOwner = "Only this machine's owner can hand off a session to it";
    public const string Generic = "The hand-off couldn't be started.";

    public static string For(string? reason) {
        if (string.IsNullOrWhiteSpace(reason)) return Generic;
        var match = CodePrefix().Match(reason);
        var code = match.Success ? match.Groups["code"].Value : null;
        switch (code) {
            case "handoff_start_changed": return StartChanged;
            case "handoff_source_unavailable": return SourceUnavailable;
            case "invalid_handoff": return InvalidHandoff;
            case "handoff_unsupported_daemon": return HandoffComposerViewModel.DaemonTooOld;
            case "handoff_not_owner": return NotOwner;
            case Capacitor.Cli.Core.LocalIpc.HandoffRefusals.RepoMismatch: return RepoMismatch;
            case Capacitor.Cli.Core.LocalIpc.HandoffRefusals.RepoNotAllowed: return RepoNotAllowed;
        }
        var friendly = HomeViewModel.FriendlyLaunchFailure(reason);
        if (friendly != reason || code is null) return friendly;
        var detail = reason[match.Length..].Trim();
        return detail.Length > 0 ? HomeViewModel.FriendlyLaunchFailure(detail) : Generic;
    }

    /// A failed resume, worded as the fallback the composer then offers.
    public static string ResumeFallback(string? reason) =>
        $"Couldn't resume this session here ({ResumeReason(reason)}). Hand off starts a new session with its context.";

    static string ResumeReason(string? reason) {
        if (string.IsNullOrWhiteSpace(reason)) return "it didn't start";
        var match = CodePrefix().Match(reason);
        return (match.Success ? match.Groups["code"].Value : null) switch {
            "resume_source_active"         => "it's active again",
            "resume_source_reserved"       => "it's being resumed or handed off elsewhere",
            "resume_unsupported_daemon"    => "your local kcap daemon can't resume sessions yet",
            "resume_directory_missing"     => "its directory is gone",
            "resume_directory_not_allowed" => "your local kcap daemon isn't allowed to work in its directory",
            "resume_failed"                => "the agent couldn't pick it up",
            "resume_other_machine" or "resume_not_resumable" or "resume_store_changed" or "resume_unsupported_vendor"
             or "resume_invalid" or "resume_not_owner" or "resume_source_unavailable" => "what it ran under isn't available here",
            _ => HomeViewModel.FriendlyLaunchFailure(reason).TrimEnd('.'),
        };
    }

    [GeneratedRegex(@"^(?<code>[a-z][a-z0-9]*(?:_[a-z0-9]+)+)(?::|$)")]
    private static partial Regex CodePrefix();
}
