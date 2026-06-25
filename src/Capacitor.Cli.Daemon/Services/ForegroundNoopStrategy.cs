using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Interactive foreground: never auto-restart; the queued marker + this log line tell the user to restart.</summary>
internal sealed partial class ForegroundNoopStrategy(ILogger<ForegroundNoopStrategy> logger) : IRestartStrategy {
    public void Restart() => LogForegroundPending(logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update pending: foreground daemon — exit and restart to apply the update")]
    static partial void LogForegroundPending(ILogger logger);
}
