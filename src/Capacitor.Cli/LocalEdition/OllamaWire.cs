using System.Text.Json.Serialization;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The bodies of the Ollama endpoints <see cref="OllamaClient"/> calls.</summary>
static partial class OllamaWire {
    internal sealed class VersionResponse {
        public string? Version { get; set; }
    }

    internal sealed class TagsResponse {
        public List<TagModel?>? Models { get; set; }
    }

    internal sealed class TagModel {
        public string? Name  { get; set; }
        public string? Model { get; set; }
    }

    internal sealed class PullRequest {
        public required string Model  { get; init; }
        public          bool   Stream { get; init; } = true;
    }

    /// <summary>One line of a streamed pull: a status, a layer's progress, or an error.</summary>
    internal sealed class PullStatus {
        public string? Status    { get; set; }
        public string? Digest    { get; set; }
        public long?   Total     { get; set; }
        public long?   Completed { get; set; }
        public string? Error     { get; set; }
    }

    internal sealed class EmbedRequest {
        public required string   Model    { get; init; }
        public required string[] Input    { get; init; }
        public          bool     Truncate { get; init; }
    }

    internal sealed class EmbedResponse {
        public List<float[]?>? Embeddings { get; set; }
        public string?         Error      { get; set; }
    }

    internal sealed class ErrorResponse {
        public string? Error { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(VersionResponse))]
    [JsonSerializable(typeof(TagsResponse))]
    [JsonSerializable(typeof(PullRequest))]
    [JsonSerializable(typeof(PullStatus))]
    [JsonSerializable(typeof(EmbedRequest))]
    [JsonSerializable(typeof(EmbedResponse))]
    [JsonSerializable(typeof(ErrorResponse))]
    internal partial class Context : JsonSerializerContext;
}
