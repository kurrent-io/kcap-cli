using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// What TryLaunchAgent answers: the agent id the server accepted the launch under, or why it refused.
public sealed record LaunchResponse {
    [JsonPropertyName("agent_id")] public string? AgentId { get; init; }
    [JsonPropertyName("refusal")]  public string? Refusal { get; init; }
}
