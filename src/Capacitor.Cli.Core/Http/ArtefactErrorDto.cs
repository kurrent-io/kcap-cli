using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>A refusal the server named. <see cref="Limit"/> carries the ceiling that was hit, so the
/// command can say which one rather than making the caller find it by bisection.</summary>
public record ArtefactErrorDto(
    [property: JsonPropertyName("code")]    string  Code,
    [property: JsonPropertyName("message")] string  Message,
    [property: JsonPropertyName("limit")]   long?   Limit);
