namespace Capacitor.Cli.Commands;

/// <summary>How a running daemon answered being stopped for a service reload.</summary>
enum ReloadStop {
    /// <summary>It accepted the shutdown and exited within the wait.</summary>
    Exited,
    /// <summary>It accepted the shutdown and was still exiting at the end of the wait.</summary>
    Overran,
    /// <summary>It closed the socket on the shutdown frame and accepted the restart sent in its place.</summary>
    Restarting,
    /// <summary>It closed the socket on the shutdown frame and refused the restart sent in its place.</summary>
    RestartRefused,
    /// <summary>It did not answer the shutdown request.</summary>
    NoAnswer,
}
