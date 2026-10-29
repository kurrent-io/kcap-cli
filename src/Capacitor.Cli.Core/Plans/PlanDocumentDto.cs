using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// A declared document. `Kind` is plan|spec|design and `Path` is relative to the repository root;
/// the body never rides this route.
public sealed record PlanDocumentDto {
    [JsonPropertyName("document_key")] public string DocumentKey { get; init; } = "";
    [JsonPropertyName("kind")]         public string Kind        { get; init; } = "";
    [JsonPropertyName("path")]         public string Path        { get; init; } = "";
}
