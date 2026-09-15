namespace Capacitor.Cli.Daemon.Services;

/// The wordings behind an attachment refusal. Both lanes refuse the same cases — the local frame so
/// the composer can word the ack, the delivery core for a server dispatch that never passes through
/// it — and a refusal the two lanes spell differently reads as two different rules.
internal static class AttachmentRefusals {
    public const string ReviewParticipant   = "attachments are not accepted by a review participant";
    public const string NeedsOwnedWorktree  = "attachments need a daemon-owned worktree";
    public const string QuitTakesNone       = "a quit command takes no attachments";
}
