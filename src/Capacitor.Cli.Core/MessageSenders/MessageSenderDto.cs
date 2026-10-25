using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.MessageSenders;

/// Who sent one user message, with every key the server holds for it; a row can match on any of them.
public sealed record MessageSenderDto {
    [JsonPropertyName("event_number")]   public long?   EventNumber  { get; init; }
    [JsonPropertyName("event_id")]       public Guid?   EventId      { get; init; }
    [JsonPropertyName("dispatch_id")]    public Guid?   DispatchId   { get; init; }
    [JsonPropertyName("sender_user_id")] public string  SenderUserId { get; init; } = "";
    [JsonPropertyName("sender_name")]    public string? SenderName   { get; init; }
    [JsonPropertyName("avatar_url")]     public string? AvatarUrl    { get; init; }
    [JsonPropertyName("sent_by_caller")] public bool    SentByCaller { get; init; }
}
