using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handler for DaemonSettingsPut. Trust model: the 0600 socket's owner, like every
/// other local frame. Changes the live config only; persisting a value is the caller's job.
internal sealed partial class DaemonSettingsIpc(
    DaemonConfig config, AgentOrchestrator orchestrator, DaemonStatusNotifier notifier, ILogger<DaemonSettingsIpc> logger) {

    public async Task HandlePutAsync(string payload, Stream stream, CancellationToken ct) {
        DaemonSettingsAckDto ack;
        try {
            ack = Apply(JsonSerializer.Deserialize(payload, SettingsIpcJsonContext.Default.DaemonSettingsPutDto));
        } catch (JsonException) {
            ack = Refuse(DaemonSettingsReasons.Malformed);
        }
        var json = JsonSerializer.Serialize(ack, SettingsIpcJsonContext.Default.DaemonSettingsAckDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.SettingsJson(FrameType.DaemonSettingsAck, json), ct);
    }

    // Validate everything before applying anything, so a put is all-or-nothing as settings grow.
    DaemonSettingsAckDto Apply(DaemonSettingsPutDto? dto) {
        if (!SettingsWire.HasAnySetting(dto)) return Refuse(DaemonSettingsReasons.Malformed);
        if (dto!.MaxAgents is < 1) return Refuse(DaemonSettingsReasons.InvalidMaxAgents);

        if (dto.MaxAgents is { } max) {
            config.MaxConcurrentAgents = max;
            notifier.Pulse();
            orchestrator.RepublishRegistration("settings");
            LogCapacity(max);
        }
        return new DaemonSettingsAckDto(true, null, config.MaxConcurrentAgents);
    }

    DaemonSettingsAckDto Refuse(string reason) => new(false, reason, config.MaxConcurrentAgents);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capacity set to {MaxAgents} agents over the local control socket")]
    partial void LogCapacity(int maxAgents);
}
