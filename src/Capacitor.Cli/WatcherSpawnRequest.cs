namespace Capacitor.Cli;

/// <summary>
/// One watcher to launch. <c>SessionIdOverride</c> separates the pid-file key from the session the
/// watcher streams: a subagent's key is <c>{session}-{agent}</c>, but the session it reports is the
/// parent's.
/// </summary>
public sealed record WatcherSpawnRequest(
    string  Key,
    string  TranscriptPath,
    string? AgentId,
    string? SessionIdOverride = null,
    string? Cwd               = null,
    bool    SkipTitle         = false,
    string  Vendor            = "claude");
