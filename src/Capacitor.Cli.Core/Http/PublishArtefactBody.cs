using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record PublishArtefactBody(
    [property: JsonPropertyName("title")]       string                  Title,
    [property: JsonPropertyName("html")]        string                  Html,
    [property: JsonPropertyName("description")] string?                 Description,
    [property: JsonPropertyName("visibility")]  string?                 Visibility,
    [property: JsonPropertyName("grants")]      List<ArtefactGrantDto>? Grants,
    [property: JsonPropertyName("sources")]     List<string>?           Sources);
