using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record ArtefactListDto([property: JsonPropertyName("artefacts")] List<ArtefactDto> Artefacts);
