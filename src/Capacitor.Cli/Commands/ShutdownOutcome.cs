namespace Capacitor.Cli.Commands;

/// <summary>How a daemon answered a shutdown request.</summary>
enum ShutdownOutcome {
    /// <summary>It acknowledged and its process was gone within the wait.</summary>
    Exited,
    /// <summary>It acknowledged and its process was still alive at the end of the wait.</summary>
    Overran,
    /// <summary>It was asked to stop only when idle and answered that it is busy.</summary>
    Busy,
    /// <summary>It answered with an error frame, or closed the connection without a reply as a daemon
    /// that cannot decode the shutdown frame does.</summary>
    Refused,
    /// <summary>No acknowledgement: unreachable, silent until the deadline, or an unexpected reply.</summary>
    NoAnswer,
}
