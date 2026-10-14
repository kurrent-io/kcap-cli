using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.NextWork;

public sealed record NextWorkFeedEvidenceDto {
    [JsonPropertyName("source")]  public string  Source  { get; init; } = "";
    [JsonPropertyName("id")]      public string  Id      { get; init; } = "";
    [JsonPropertyName("href")]    public string? Href    { get; init; }
    [JsonPropertyName("summary")] public string  Summary { get; init; } = "";
}
