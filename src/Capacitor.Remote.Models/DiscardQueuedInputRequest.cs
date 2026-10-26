using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record DiscardQueuedInputRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("dispatch_id")] Guid DispatchId);
