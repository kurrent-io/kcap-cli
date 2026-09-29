using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record SetArtefactVisibilityBody(
    [property: JsonPropertyName("visibility")] string                  Visibility,
    [property: JsonPropertyName("grants")]     List<ArtefactGrantDto>? Grants);
