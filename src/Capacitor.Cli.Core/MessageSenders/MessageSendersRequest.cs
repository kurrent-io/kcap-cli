using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.MessageSenders;

/// The body of `POST /api/sessions/{id}/message-senders`: user messages by whichever key the caller holds.
/// Every list is sent, empty or not: the server reads a null list as a malformed request.
public sealed record MessageSendersRequest {
    /// The server refuses a request carrying more keys than this, across all three lists.
    public const int MaxKeys = 500;

    [JsonPropertyName("event_ids")]     public List<Guid> EventIds     { get; init; } = [];
    [JsonPropertyName("event_numbers")] public List<long> EventNumbers { get; init; } = [];
    [JsonPropertyName("dispatch_ids")]  public List<Guid> DispatchIds  { get; init; } = [];
}
