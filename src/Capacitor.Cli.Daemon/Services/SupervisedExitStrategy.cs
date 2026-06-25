using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Service-managed: flag the restart exit code and shut down; the supervisor relaunches the new binary.</summary>
internal sealed partial class SupervisedExitStrategy(
        RestartState state, IHostApplicationLifetime lifetime, ILogger<SupervisedExitStrategy> logger
    ) : IRestartStrategy {
    public void Restart() {
        LogSupervisedRestart(logger);
        state.SupervisedRestart = true;
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update: exiting non-zero for supervisor relaunch")]
    static partial void LogSupervisedRestart(ILogger logger);
}
