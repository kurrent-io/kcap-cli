namespace Capacitor.Cli.Daemon.Services;

/// <summary>The OS-specific action that applies a queued restart. Selected once at startup.</summary>
public interface IRestartStrategy {
    void Restart();
}
