namespace Capacitor.Cli.Commands;

/// <summary>How a daemon answered a shutdown request.</summary>
enum ShutdownOutcome {
    /// <summary>It acknowledged and its process was gone within the wait.</summary>
    Exited,
    /// <summary>It acknowledged and its process was still alive at the end of the wait.</summary>
    Overran,
    /// <summary>It answered with an error frame: a daemon that predates the shutdown frame.</summary>
    Refused,
    /// <summary>No acknowledgement: unreachable, silent, or an unexpected reply.</summary>
    NoAnswer,
}
