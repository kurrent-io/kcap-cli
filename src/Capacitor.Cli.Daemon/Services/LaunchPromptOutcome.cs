namespace Capacitor.Cli.Daemon.Services;

/// <summary>How a hosted agent's launch prompt ended up.</summary>
internal enum LaunchPromptOutcome {
    /// <summary>Pasted once startup output had settled.</summary>
    Delivered,

    /// <summary>Pasted blind after the startup wait expired, into a TUI that never went quiet.</summary>
    DeliveredAfterMaxWait,

    /// <summary>Withdrawn: the agent exited before its prompt could be pasted.</summary>
    AgentExited,

    /// <summary>Withdrawn: a graceful stop was requested first.</summary>
    StopRequested,

    /// <summary>Withdrawn: the runtime was disposed first.</summary>
    Cancelled,

    /// <summary>The paste or its submit could not be written: the PTY had closed.</summary>
    WriteFailed,
}
