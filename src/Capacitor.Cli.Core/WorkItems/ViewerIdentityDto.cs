using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// The body of <c>GET /api/users/me/identity</c>: the caller's canonical id, every id merged into it,
/// and the canonical account's picture. A server older than the picture omits it.
public sealed record ViewerIdentityDto {
    [JsonPropertyName("user_id")]    public required string       UserId    { get; init; }
    [JsonPropertyName("avatar_url")] public string?               AvatarUrl { get; init; }
    [JsonPropertyName("ids")]        public IReadOnlyList<string> Ids       { get; init; } = [];

    /// A bare number is a legacy GitHub id; the server lists it as `github:{n}`.
    public bool Owns(string userId) {
        var id = userId.Length > 0 && userId.All(char.IsAsciiDigit) ? $"github:{userId}" : userId;
        return id == UserId || Ids.Contains(id);
    }
}
