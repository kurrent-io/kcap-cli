namespace Capacitor.Cli;

/// <summary>What this machine knows about a session's agent process.</summary>
enum SessionLiveness {
    /// <summary>No process here ran it, or its record has been pruned.</summary>
    Unknown,
    Running,
    Exited
}
