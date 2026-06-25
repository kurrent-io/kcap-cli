namespace Capacitor.Cli.Daemon.Services;

/// <summary>Result of applying a restart, so the coordinator knows whether it actually happened.</summary>
public enum RestartOutcome {
    /// <summary>Shutdown/respawn was initiated — the coordinator is done (terminal).</summary>
    Initiated,
    /// <summary>The restart failed to start (e.g. respawn spawn error); keep the request pending and retry.</summary>
    Retry,
    /// <summary>Deliberately did nothing (foreground daemon) — terminal; a manual restart is required.</summary>
    NoOp,
}

/// <summary>The OS-specific action that applies a queued restart. Selected once at startup.</summary>
public interface IRestartStrategy {
    RestartOutcome Restart();
}
