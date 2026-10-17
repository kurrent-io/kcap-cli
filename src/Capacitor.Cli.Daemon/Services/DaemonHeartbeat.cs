using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Touches the daemon's heartbeat file every <see cref="Interval"/> so a successor can tell how long
/// ago its predecessor was last alive.
/// </summary>
internal sealed class DaemonHeartbeat(DaemonConfig config, TimeProvider time, ILogger<DaemonHeartbeat> logger) : BackgroundService {
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            _ = config.Store;
        } catch (InvalidOperationException) {
            logger.LogInformation("No daemon store configured; heartbeat disabled");
            return;
        }

        // The first beat waits a full interval: the successor reads the predecessor's stamp right
        // after startup, and an immediate beat would overwrite it.
        using var timer = new PeriodicTimer(Interval, time);

        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Beat();
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal void Beat() {
        var path = config.Store.HeartbeatPath(config.Name);

        try {
            config.Store.EnsureDirectory();
            if (!File.Exists(path)) File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, time.GetUtcNow().UtcDateTime);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            logger.LogWarning(e, "Could not write the heartbeat file {Path}", path);
        }
    }

    public static DateTimeOffset? LastBeat(DaemonStore store, string name) {
        var path = store.HeartbeatPath(name);

        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }
}
