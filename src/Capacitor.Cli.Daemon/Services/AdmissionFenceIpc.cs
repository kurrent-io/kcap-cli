using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Local-socket handler for the rename fence. The connection that acquires the fence holds it:
/// commit and abort arrive on it, and its end releases a fence that was not committed.
/// </summary>
internal sealed partial class AdmissionFenceIpc(
        DaemonConfig config, AdmissionFence fence, AgentOrchestrator orchestrator, EvalContextCache evalCache,
        ILogger<AdmissionFenceIpc> logger) {

    public async Task HandleAcquireAsync(string payload, Stream stream, CancellationToken ct) {
        AdmissionFenceAcquireDto? request;
        try {
            request = JsonSerializer.Deserialize(payload, AdmissionFenceIpcJsonContext.Default.AdmissionFenceAcquireDto);
        } catch (JsonException) {
            request = null;
        }

        if (request is null) {
            await ReplyAsync(stream, Refuse(AdmissionFenceWire.Malformed), ct);
            return;
        }
        if (!string.Equals(request.ExpectedName, config.Name, StringComparison.Ordinal)) {
            await ReplyAsync(stream, Refuse(AdmissionFenceWire.IdentityMismatch), ct);
            return;
        }

        switch (fence.TryAcquire(() => orchestrator.EffectiveCount > 0 || evalCache.Count > 0, out var hold)) {
            case AdmissionFence.AcquireResult.Busy:
                await ReplyAsync(stream, Refuse(AdmissionFenceWire.Busy), ct);
                return;
            case AdmissionFence.AcquireResult.Fenced:
                await ReplyAsync(stream, Refuse(AdmissionFenceWire.Fenced), ct);
                return;
        }

        LogHeld();
        try {
            await ReplyAsync(stream, Ack(AdmissionFenceWire.Held), ct);
            await ServeHoldAsync(hold!, stream, ct);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) {
            // The connection ended; the finally below decides what that means for the fence.
        } finally {
            hold!.Close();
            LogConnectionClosed(fence.IsFenced);
        }
    }

    async Task ServeHoldAsync(AdmissionFence.Hold hold, Stream stream, CancellationToken ct) {
        while (await FrameCodec.ReadAsync(stream, ct) is { } frame) {
            switch (frame.Type) {
                case FrameType.AdmissionFenceCommit:
                    if (hold.Commit()) {
                        LogCommitted();
                        await ReplyAsync(stream, Ack(AdmissionFenceWire.Committed), ct);
                    } else {
                        await ReplyAsync(stream, Refuse(AdmissionFenceWire.CommitFailed), ct);
                    }
                    break;
                case FrameType.AdmissionFenceAbort:
                    if (hold.Abort()) {
                        LogAborted();
                        await ReplyAsync(stream, Ack(AdmissionFenceWire.Aborted), ct);
                    } else {
                        await ReplyAsync(stream, Refuse(AdmissionFenceWire.AbortFailed), ct);
                    }
                    return;
                default:
                    await ReplyAsync(stream, Refuse(AdmissionFenceWire.Malformed), ct);
                    break;
            }
        }
    }

    AdmissionFenceAckDto Ack(string state) => new(true, null, state, Environment.ProcessId, config.InstanceId);

    AdmissionFenceAckDto Refuse(string reason) => new(false, reason, null, Environment.ProcessId, config.InstanceId);

    static Task ReplyAsync(Stream stream, AdmissionFenceAckDto ack, CancellationToken ct) =>
        FrameCodec.WriteAsync(stream,
            LocalFrame.FenceJson(FrameType.AdmissionFenceAck, JsonSerializer.Serialize(ack, AdmissionFenceIpcJsonContext.Default.AdmissionFenceAckDto)), ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rename fence held; refusing new work")]
    partial void LogHeld();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rename fence committed; retirement is about to begin")]
    partial void LogCommitted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rename fence aborted; admitting new work again")]
    partial void LogAborted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Rename fence connection closed (still fenced: {Fenced})")]
    partial void LogConnectionClosed(bool fenced);
}
