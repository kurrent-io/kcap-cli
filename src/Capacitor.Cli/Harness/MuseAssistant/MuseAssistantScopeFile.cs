using System.Text.Json.Serialization;

namespace Capacitor.Cli.Harness.MuseAssistant;

public sealed record MuseAssistantScopeFile(
        [property: JsonPropertyName("server_url")] string   ServerUrl,
        [property: JsonPropertyName("user_id")]    string   UserId,
        [property: JsonPropertyName("roles")]      string[] Roles,
        [property: JsonPropertyName("chats")]      string[] Chats
    );
