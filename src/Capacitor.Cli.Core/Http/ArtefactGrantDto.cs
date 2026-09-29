using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record ArtefactGrantDto(
    [property: JsonPropertyName("grant_type")]   string GrantType,
    [property: JsonPropertyName("grantee_id")]   string GranteeId,
    [property: JsonPropertyName("grantee_name")] string GranteeName);
