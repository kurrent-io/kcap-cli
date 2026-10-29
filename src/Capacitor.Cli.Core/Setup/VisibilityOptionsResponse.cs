using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup;

/// <summary>Body of <c>GET /api/users/me/visibility-options</c>: the stops to offer, in order, and the one to
/// preselect</summary>
public sealed record VisibilityOptionsResponse {
    [JsonPropertyName("options")]            public List<VisibilityOptionDto> Options           { get; init; } = [];
    [JsonPropertyName("default_visibility")] public string?                   DefaultVisibility { get; init; }
}
