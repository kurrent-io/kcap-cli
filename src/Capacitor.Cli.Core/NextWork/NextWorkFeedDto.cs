using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.NextWork;

public sealed record NextWorkFeedDto {
    List<NextWorkFeedRowDto> _items = [];

    [JsonPropertyName("generated_at")] public DateTimeOffset?          GeneratedAt { get; init; }
    [JsonPropertyName("items")]        public List<NextWorkFeedRowDto> Items       { get => _items; init => _items = value ?? []; }
}
