namespace Capacitor.Cli.Daemon.Services;

/// <summary>An instant inside a standalone snapshot at which a caller can be held or failed.</summary>
public enum SnapshotPoint {
    /// <summary>Before the claim is attempted, while the destination is still absent.</summary>
    PreClaim,

    /// <summary>After the claim file exists but before the destination is created.</summary>
    PostClaim,

    /// <summary>Inside the claimant's rollback, after the tree is deleted and before the claim is released.</summary>
    Rollback,

    /// <summary>After the snapshot tree is copied, where a failure must unwind the claim.</summary>
    TreeCopied,
}
