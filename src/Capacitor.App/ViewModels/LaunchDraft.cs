namespace Capacitor.App.ViewModels;

/// Everything one launch sends, captured before the upload starts. The request is built from this
/// and never from the live properties: an upload takes time the user can spend re-pointing the
/// launcher, and a file must reach the machine the composer was aimed at. GoalEdits and
/// TrayGeneration are what the composer looked like at capture, so a launch only clears what it
/// actually sent.
public sealed record LaunchDraft(
    string Machine, bool Remote, string RepoPath, string Vendor, string Goal, int GoalEdits,
    string Model, string? Effort, string? PermissionMode, IReadOnlyList<StagedAttachment> Files,
    int TrayGeneration);
