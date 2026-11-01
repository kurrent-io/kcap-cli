using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One page the server's search matched. <see cref="OwnerUserId"/> is the stored owner id,
/// possibly a legacy one, so whether the page is the caller's is <see cref="IsOwner"/>.
/// <see cref="MatchedIn"/> may carry a value newer than this binary knows.</summary>
public record PageSearchHitDto(
    [property: JsonPropertyName("artefact_id")]        string         ArtefactId,
    [property: JsonPropertyName("title")]              string         Title,
    [property: JsonPropertyName("owner_user_id")]      string         OwnerUserId,
    [property: JsonPropertyName("owner_display_name")] string?        OwnerDisplayName,
    [property: JsonPropertyName("visibility")]         string         Visibility,
    [property: JsonPropertyName("latest_version")]     int            LatestVersion,
    [property: JsonPropertyName("updated_at")]         DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("is_owner")]           bool           IsOwner,
    [property: JsonPropertyName("url")]                string         Url,
    [property: JsonPropertyName("excerpt")]            string         Excerpt,
    [property: JsonPropertyName("matched_in")]         string         MatchedIn);
