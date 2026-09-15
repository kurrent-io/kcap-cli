namespace Capacitor.Cli.Daemon.Services;

/// Where a fetched attachment lands for an agent: inside its worktree (relative trailer paths, what
/// a workspace-confined file tool reads) or in a daemon-owned directory outside every cwd (absolute
/// paths, for a runtime whose OS sandbox stops it writing there — so it cannot steer the write).
internal enum AttachmentPlacement { Worktree, DaemonStore }
