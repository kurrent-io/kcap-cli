using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands.Capture.Wire;

internal sealed record CaptureRepairSourceRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("agent_id")] string? AgentId,
    [property: JsonPropertyName("vendor")] string Vendor);
