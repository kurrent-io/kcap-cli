namespace Capacitor.Cli.Core;

/// <summary>Daemon process exit codes wrappers/supervisors interpret.</summary>
public static class ExitCodes {
    /// <summary>
    /// Controlled restart-after-update for a supervised daemon. Non-zero so the
    /// failure-restart policy relaunches us (launchd KeepAlive/SuccessfulExit=false,
    /// systemd Restart=on-failure). Distinct from 1 (config error) and 2/3 (name-in-use).
    /// </summary>
    public const int RestartRequested = 10;
}
