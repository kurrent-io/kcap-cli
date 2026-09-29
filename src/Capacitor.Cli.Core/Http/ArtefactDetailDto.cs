using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record ArtefactDetailDto(
    [property: JsonPropertyName("artefact")] ArtefactDto             Artefact,
    [property: JsonPropertyName("grants")]   List<ArtefactGrantDto>? Grants);
