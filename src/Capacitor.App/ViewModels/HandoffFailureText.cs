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

    [GeneratedRegex(@"^(?<code>[a-z][a-z0-9]*(?:_[a-z0-9]+)+)(?::|$)")]
    private static partial Regex CodePrefix();
}
