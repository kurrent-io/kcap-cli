namespace Capacitor.Cli.Commands;

/// <summary>How a running daemon answered being stopped for a service reload.</summary>
enum ReloadStop {
    /// <summary>It accepted the shutdown and exited within the wait.</summary>
    Exited,
    /// <summary>It accepted the shutdown and was still exiting at the end of the wait.</summary>
    Overran,
    /// <summary>It predates the shutdown frame and accepted a restart instead.</summary>
    Restarting,
    /// <summary>It predates the shutdown frame and refused the restart.</summary>
    RestartRefused,
    /// <summary>It did not answer the shutdown request.</summary>
    NoAnswer,
}
