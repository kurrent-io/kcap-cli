using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup;

/// <summary>One <c>default_visibility</c> stop the tenant can honour, in the copy the browser's first-run flow shows</summary>
public sealed record VisibilityOptionDto {
    [JsonPropertyName("key")]    public string? Key    { get; init; }
    [JsonPropertyName("label")]  public string? Label  { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}
