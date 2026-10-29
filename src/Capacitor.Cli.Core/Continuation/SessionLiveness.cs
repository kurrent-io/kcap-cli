namespace Capacitor.Cli.Core.Continuation;

/// <summary>What this machine knows about a session's agent process.</summary>
public enum SessionLiveness {
    /// <summary>No process here ran it, or its record has been pruned.</summary>
    Unknown,
    Running,
    Exited
}
