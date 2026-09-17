using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One prompt the server holds for an agent's next turn: what SubscribeToChat returns and
/// PendingInputChanged pushes. Nothing is required, so one thin item cannot drop a whole push.
public sealed record QueuedInputItem {
    [JsonPropertyName("dispatch_id")]    public Guid DispatchId { get; init; }
    [JsonPropertyName("sender_user_id")] public string? SenderUserId { get; init; }
    [JsonPropertyName("text")]           public string Text { get; init; } = "";
    [JsonPropertyName("dispatched_at")]  public DateTimeOffset DispatchedAt { get; init; }
}
