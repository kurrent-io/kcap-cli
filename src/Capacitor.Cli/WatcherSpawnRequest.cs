namespace Capacitor.Cli;

/// <summary>
/// One watcher to launch. <c>SessionIdOverride</c> separates the pid-file key from the session the
/// watcher streams: a subagent's key is <c>{session}-{agent}</c>, but the session it reports is the
/// parent's. <c>ParentPid</c> names the agent process the watcher's exit follows when the caller knows
/// it; left null, the spawner resolves it from its own ancestry, which only a hook can do.
/// </summary>
public sealed record WatcherSpawnRequest(
    string  Key,
    string  TranscriptPath,
    string? AgentId,
    string? SessionIdOverride = null,
    string? Cwd               = null,
    bool    SkipTitle         = false,
    string  Vendor            = "claude",
    int?    ParentPid         = null) {
    /// <summary>Where a spawn failure is reported; null is the process's stderr.</summary>
    public TextWriter? Stderr { get; init; }
}
