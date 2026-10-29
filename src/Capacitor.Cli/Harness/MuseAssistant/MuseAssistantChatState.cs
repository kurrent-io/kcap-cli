using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>One chat's recording state. Grant and watermark share a file so neither can outlive the
/// other; server and user are stored so a file in the wrong directory is refused.</summary>
public sealed record MuseAssistantChatState(
        [property: JsonPropertyName("chat_id")]             string                    ChatId,
        [property: JsonPropertyName("server_url")]          string                    ServerUrl,
        [property: JsonPropertyName("user_id")]             string                    UserId,
        [property: JsonPropertyName("grant")]               MuseAssistantGrant        Grant,
        [property: JsonPropertyName("watermark")]           long?                     Watermark,
        [property: JsonPropertyName("last_selected_at_ms")] long?                     LastSelectedAtMs,
        [property: JsonPropertyName("open")]                MuseAssistantOpenSession? Open
    );
