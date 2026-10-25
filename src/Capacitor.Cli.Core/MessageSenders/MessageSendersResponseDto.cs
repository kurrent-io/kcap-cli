using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.MessageSenders;

/// A message nobody is recorded as sending is absent.
public sealed record MessageSendersResponseDto {
    /// Null when the field is absent or null: neither is a readable answer.
    [JsonPropertyName("senders")] public List<MessageSenderDto>? Senders { get; init; }
}
