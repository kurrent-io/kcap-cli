using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Local-socket handler for a client-requested shutdown. The intent is set before the exit begins so
/// the orchestrator's shutdown step stops or preserves agents accordingly. A relaunch goes through
/// the restart coordinator's forced path and the daemon's own restart strategy; a final stop never
/// waits on evals unless the request asks it to stop only when idle.
/// </summary>
internal sealed partial class DaemonShutdownIpc(
        AgentOrchestrator orchestrator, RestartCoordinator restart, IHostApplicationLifetime lifetime,
        ILogger<DaemonShutdownIpc> logger, StopCause? stopCause = null) {

    public async Task HandleAsync(string text, Stream stream, CancellationToken ct) {
        var request = ShutdownIpc.Parse(text);
        if (request is null) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error("shutdown: malformed request"), ct);

            return;
        }

        if (request.WhenIdle && restart.IsHardBusy()) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error("busy: an eval or a launch in admission is in progress"), ct);

            return;
        }

        orchestrator.ShutdownIntent = request.Agents == "end" ? ShutdownIntent.EndAgents : ShutdownIntent.KeepAgents;
        LogShutdown(request.Agents, request.Exit);

        if (request.Exit == "final") {
            await FrameCodec.WriteAsync(stream, LocalFrame.ShutdownAck("stopping"), ct);
            stopCause?.Note($"a local stop request (agents={request.Agents})");
            lifetime.StopApplication();

            return;
        }

        var reply = restart.RequestRestart(force: true) switch {
            RestartRequestResult.Restarting     => LocalFrame.ShutdownAck("stopping"),
            RestartRequestResult.Queued         => LocalFrame.ShutdownAck("queued"),
            RestartRequestResult.Failed         => LocalFrame.Error("shutdown: restart failed to start; the daemon will retry"),
            RestartRequestResult.ManualRequired => LocalFrame.Error("shutdown: foreground daemon — exit and restart it manually"),
            _                                   => LocalFrame.Error("shutdown: unknown restart result"),
        };
        await FrameCodec.WriteAsync(stream, reply, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown requested: agents={Agents} exit={Exit}")]
    partial void LogShutdown(string agents, string exit);
}
