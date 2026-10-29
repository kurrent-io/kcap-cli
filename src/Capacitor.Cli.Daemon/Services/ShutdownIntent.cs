namespace Capacitor.Cli.Daemon.Services;

/// What a daemon shutdown does to its hosted agents.
internal enum ShutdownIntent {
    /// Stop interactive agents so a successor daemon resumes them.
    KeepAgents,

    /// End every agent and report it ended.
    EndAgents
}
