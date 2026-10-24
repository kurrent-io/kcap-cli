using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// The body of <c>GET /api/users/me/identity</c>: the caller's canonical id, every id merged into it,
/// and the canonical account's picture. A server older than the picture omits it.
public sealed record ViewerIdentityDto {
    [JsonPropertyName("user_id")]    public required string       UserId    { get; init; }
    [JsonPropertyName("avatar_url")] public string?               AvatarUrl { get; init; }
    [JsonPropertyName("ids")]        public IReadOnlyList<string> Ids       { get; init; } = [];

    public bool Owns(string userId) => userId == UserId || Ids.Contains(userId);
}
